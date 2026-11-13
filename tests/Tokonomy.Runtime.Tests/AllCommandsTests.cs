using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Executors;

namespace Tokonomy.Runtime.Tests;

/// <summary>"Allow all shell commands": the per-CLI switches, the scaffolded config and the Antigravity audit.</summary>
public class AllCommandsTests
{
    private static readonly string Workdir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tokonomy-agy-wd"));

    private static ExecutorsFile Parse(string yaml)
    {
        var dir = Directory.CreateTempSubdirectory("tk-allcmd-");
        try
        {
            var path = Path.Combine(dir.FullName, "executors.yaml");
            File.WriteAllText(path, yaml);
            var issues = new List<ConfigIssue>();
            var file = ConfigLoader.Parse<ExecutorsFile>(path, issues);
            Assert.Empty(issues);
            return file!;
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static SetupChoices Choices(string shell) => new()
    {
        Strong = Connection.Claude,
        Cheap = Connection.Antigravity,
        ArchitectModel = "a",
        WorkerModel = "w",
        WorkerShell = shell,
    };

    [Fact]
    public void Restricted_is_the_default_and_no_executor_allows_all_commands()
    {
        var file = Parse(Scaffolder.ExecutorsYamlFor(Choices("restricted")));
        Assert.All(file.Executors.Values, e => Assert.False(e.Permissions.AllowAllCommands));
        Assert.All(Parse(Scaffolder.ExecutorsYaml).Executors.Values, e => Assert.False(e.Permissions.AllowAllCommands));
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("claude-code")]
    [InlineData("claude-code-sub")]
    [InlineData("codex")]
    [InlineData("antigravity")]
    public void Enabling_all_commands_touches_only_the_named_executor_and_keeps_the_file_valid(string name)
    {
        var yaml = Scaffolder.EnableAllCommands(Scaffolder.ExecutorsYaml.ReplaceLineEndings("\n"), name);
        var file = Parse(yaml);

        Assert.True(file.Executors[name].Permissions.AllowAllCommands);
        Assert.All(file.Executors.Where(kv => kv.Key != name), kv => Assert.False(kv.Value.Permissions.AllowAllCommands));
        // The rest of the entry is intact (the antigravity permission block keeps its other settings).
        var baseline = Parse(Scaffolder.ExecutorsYaml).Executors[name];
        Assert.Equal(baseline.Cmd, file.Executors[name].Cmd);
        Assert.Equal(baseline.Permissions.ScopeFilesToWorktree, file.Executors[name].Permissions.ScopeFilesToWorktree);
        Assert.Equal(baseline.Permissions.DenyTempReads, file.Executors[name].Permissions.DenyTempReads);
    }

    [Fact]
    public void The_wizard_choice_enables_the_executor_that_runs_the_worker()
    {
        var file = Parse(Scaffolder.ExecutorsYamlFor(Choices("all")));
        Assert.True(file.Executors["antigravity"].Permissions.AllowAllCommands);
        Assert.False(file.Executors["claude-code-sub"].Permissions.AllowAllCommands);
    }

    [Fact]
    public void Antigravity_gets_the_skip_permissions_flag_once()
    {
        var def = Parse(Scaffolder.ExecutorsYaml).Executors["antigravity"];
        var args = AllCommandsMode.Apply(def, AllCommandsMode.Apply(def, def.Cmd));
        Assert.Equal(1, args.Count(a => a == "--dangerously-skip-permissions"));
        Assert.Contains("--sandbox", def.Cmd);              // the restricted command line keeps it
        Assert.DoesNotContain("--sandbox", args);           // allow-all drops it: with it any shell command kills agy (exit 2)
    }

    [Fact]
    public void Claude_code_switches_to_bypass_permissions()
    {
        var def = Parse(Scaffolder.ExecutorsYaml).Executors["claude-code-sub"];
        var args = AllCommandsMode.Apply(def, def.Cmd);
        Assert.Equal("bypassPermissions", args[args.IndexOf("--permission-mode") + 1]);
        Assert.DoesNotContain("acceptEdits", args);
    }

    [Fact]
    public void Codex_switches_to_full_access()
    {
        var def = Parse(Scaffolder.ExecutorsYaml).Executors["codex"];
        var args = AllCommandsMode.Apply(def, def.Cmd);
        Assert.Equal("danger-full-access", args[args.IndexOf("--sandbox") + 1]);
        Assert.DoesNotContain("workspace-write", args);
    }

    [Fact]
    public void Opencode_opens_web_and_external_directory_access()
    {
        var def = Parse(Scaffolder.ExecutorsYaml).Executors["opencode"];
        var json = def.Files["opencode.json"];
        var opened = AllCommandsMode.ApplyToFile(def, "opencode.json", json);

        Assert.Contains("\"webfetch\": \"deny\"", json, StringComparison.Ordinal);
        Assert.Contains("\"webfetch\": \"allow\"", opened, StringComparison.Ordinal);
        Assert.Contains("\"external_directory\": \"allow\"", opened, StringComparison.Ordinal);
        Assert.Contains("\"bash\": \"allow\"", opened, StringComparison.Ordinal);
        Assert.Equal(json, AllCommandsMode.ApplyToFile(def, "other.json", json));
    }

    [Fact]
    public void A_custom_executor_declares_its_own_switch()
    {
        var custom = new ExecutorDefinition { Cmd = ["my-agent", "run", "{prompt}"], AllCommandsArgs = ["--yolo"] };
        Assert.True(AllCommandsMode.IsSupported(custom));
        Assert.Equal(["my-agent", "run", "{prompt}", "--yolo"], AllCommandsMode.Apply(custom, custom.Cmd));

        var unknown = new ExecutorDefinition { Cmd = ["my-agent", "run"] };
        Assert.False(AllCommandsMode.IsSupported(unknown));
        Assert.Equal(unknown.Cmd, AllCommandsMode.Apply(unknown, unknown.Cmd));
    }

    private static WorkerFault? Eval(string stdout, bool all) =>
        AgyRunInterpreter.Evaluate(AgyRunParser.Parse(stdout, AgyFixtures.Model), 0, "", AgyFixtures.Model, true, Workdir,
            ["python -m unittest"], allowAllCommands: all);

    [Fact]
    public void Any_command_is_a_fault_when_restricted_and_fine_when_all_are_allowed()
    {
        var stdout = AgyFixtures.Stream(
            AgyFixtures.Init(),
            AgyFixtures.Tool("run_command", "DONE", AgyFixtures.Cmd("cd backend; dotnet ef migrations add Initial")),
            AgyFixtures.Result());

        Assert.Equal(WorkerFaultKind.PermissionDenied, Eval(stdout, all: false)?.Kind);
        Assert.Null(Eval(stdout, all: true));
    }

    [Fact]
    public void A_denied_action_is_still_a_fault_when_all_commands_are_allowed()
    {
        var fault = Eval(AgyFixtures.HeadlessDenied("python -c \"print(1)\""), all: true);
        Assert.Equal(WorkerFaultKind.PermissionDenied, fault?.Kind);
    }

    [Fact]
    public void The_brief_for_an_all_commands_worker_does_not_claim_a_command_allowlist()
    {
        var settings = AgySettings.Build(Workdir, new ExecutorPermissions { AllowAllCommands = true }, []);
        var text = ConfigExecutor.AgyBriefSection(settings, ShellGrant.AnyCommand);
        Assert.Contains("You may run shell commands", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NO shell commands", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ONLY shell commands", text, StringComparison.Ordinal);
    }
}
