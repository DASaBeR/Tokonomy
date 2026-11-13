using Tokonomy.Core.Config;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Executors;

namespace Tokonomy.Runtime.Tests;

/// <summary>Per-unit shell grants: what each agent CLI is told, and how strictly it can enforce an approved list.</summary>
public class ShellAccessTests
{
    private static readonly string[] Approved = ["dotnet tool restore", "dotnet ef migrations add Initial -p src/Api -s src/Api"];

    private static ExecutorDefinition Def(string name)
    {
        var dir = Directory.CreateTempSubdirectory("tk-shell-");
        try
        {
            var path = Path.Combine(dir.FullName, "executors.yaml");
            File.WriteAllText(path, Scaffolder.ExecutorsYaml);
            return ConfigLoader.Parse<ExecutorsFile>(path, new List<ConfigIssue>())!.Executors[name];
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Theory]
    [InlineData("claude-code-sub", ShellEnforcement.PerCommand)]
    [InlineData("claude-code", ShellEnforcement.PerCommand)]
    [InlineData("opencode", ShellEnforcement.PerCommand)]
    [InlineData("antigravity", ShellEnforcement.UnitWideWithAudit)]
    [InlineData("codex", ShellEnforcement.SandboxLevel)]
    public void Each_cli_reports_how_strictly_it_can_enforce_a_list(string name, ShellEnforcement expected) =>
        Assert.Equal(expected, ShellAccess.Enforcement(Def(name)));

    [Fact]
    public void A_custom_cli_is_per_command_only_when_it_declares_how()
    {
        Assert.Equal(ShellEnforcement.Unsupported, ShellAccess.Enforcement(new ExecutorDefinition { Cmd = ["my-agent"] }));
        Assert.Equal(ShellEnforcement.UnitWideWithAudit, ShellAccess.Enforcement(new ExecutorDefinition { Cmd = ["my-agent"], AllCommandsArgs = ["--yolo"] }));
        Assert.Equal(ShellEnforcement.PerCommand, ShellAccess.Enforcement(new ExecutorDefinition { Cmd = ["my-agent"], ShellGrantArgs = ["--allow", "{command}"] }));
    }

    [Fact]
    public void No_grant_changes_nothing()
    {
        foreach (var name in new[] { "claude-code-sub", "opencode", "antigravity", "codex" })
        {
            var def = Def(name);
            Assert.Equal(def.Cmd, ShellAccess.Apply(def, def.Cmd, ShellGrant.None));
        }
    }

    [Fact]
    public void Claude_code_gets_one_allowed_tool_pattern_per_approved_command()
    {
        var def = Def("claude-code-sub");
        var args = ShellAccess.Apply(def, def.Cmd, ShellGrant.Of(Approved));
        var i = args.IndexOf("--allowedTools");
        Assert.True(i >= 0);
        Assert.Equal(["Bash(dotnet tool restore)", "Bash(dotnet ef migrations add Initial -p src/Api -s src/Api)"], args.Skip(i + 1).ToList());
        Assert.Contains("acceptEdits", args);   // not widened to bypassPermissions
    }

    [Fact]
    public void Opencode_bash_becomes_a_deny_by_default_pattern_map()
    {
        var def = Def("opencode");
        var json = ShellAccess.ApplyToFile(def, "opencode.json", def.Files["opencode.json"], ShellGrant.Of(Approved));
        Assert.Contains("\"bash\": {\"*\":\"deny\",\"dotnet tool restore\":\"allow\"", json, StringComparison.Ordinal);
        Assert.Contains("\"webfetch\": \"deny\"", json, StringComparison.Ordinal);   // other permissions untouched
    }

    [Fact]
    public void Antigravity_runs_the_unit_unrestricted_but_is_audited_against_the_list()
    {
        var def = Def("antigravity");
        var grant = ShellGrant.Of(Approved);
        var args = ShellAccess.Apply(def, def.Cmd, grant);
        Assert.Contains("--dangerously-skip-permissions", args);
        Assert.DoesNotContain("--sandbox", args);

        Assert.Empty(ShellAccess.Violations(def, grant, ["dotnet tool restore", "DOTNET TOOL RESTORE"]));
        var violations = ShellAccess.Violations(def, grant, ["dotnet tool restore", "curl http://x", "curl http://x"]);
        Assert.Equal(["curl http://x"], violations);
    }

    [Fact]
    public void Codex_keeps_its_sandbox_level_for_a_listed_grant_and_has_nothing_to_audit()
    {
        var def = Def("codex");
        var args = ShellAccess.Apply(def, def.Cmd, ShellGrant.Of(Approved));
        Assert.Equal(def.Cmd, args);
        Assert.Empty(ShellAccess.Violations(def, ShellGrant.Of(Approved), ["anything"]));
    }

    [Fact]
    public void A_custom_cli_gets_its_own_arguments_for_every_command()
    {
        var def = new ExecutorDefinition { Cmd = ["my-agent", "run"], ShellGrantArgs = ["--allow", "{command}"] };
        var args = ShellAccess.Apply(def, def.Cmd, ShellGrant.Of(Approved));
        Assert.Equal(["my-agent", "run", "--allow", Approved[0], "--allow", Approved[1]], args);
    }

    [Fact]
    public void Any_command_still_overrides_a_list()
    {
        var def = Def("claude-code-sub");
        var args = ShellAccess.Apply(def, def.Cmd, ShellGrant.AnyCommand);
        Assert.Contains("bypassPermissions", args);
        Assert.DoesNotContain("--allowedTools", args);
        Assert.Empty(ShellAccess.Violations(Def("antigravity"), ShellGrant.AnyCommand, ["whatever"]));
    }

    [Fact]
    public void Grants_are_trimmed_and_deduplicated()
    {
        var grant = ShellGrant.Of([" dotnet build ", "DOTNET BUILD", "", "dotnet test"]);
        Assert.Equal(["dotnet build", "dotnet test"], grant.Commands);
        Assert.True(ShellGrant.Of([]).IsNone);
    }

    [Fact]
    public void The_brief_lists_exactly_the_approved_commands()
    {
        var settings = AgySettings.Build(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wd")), new ExecutorPermissions(), []);
        var text = ConfigExecutor.AgyBriefSection(settings, ShellGrant.Of(Approved));
        Assert.Contains("`dotnet tool restore`", text, StringComparison.Ordinal);
        Assert.Contains("Any other command is a violation", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NO shell commands", text, StringComparison.Ordinal);
    }
}
