using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Tokonomy.Core.Model;
using Tokonomy.Core.Takeover;
using Tokonomy.Llm.Architect;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Gates;
using Tokonomy.Runtime.Ledger;

namespace Tokonomy.Runtime.Tests;

/// <summary>
/// Milestone 0: one test per known defect (D1-D10 in the private audit table). EVERY TEST HERE IS EXPECTED TO FAIL on the current code
/// (they are "red" on purpose) and must pass once the matching fix lands. Run only these with
/// <c>dotnet test --filter Category=DefectRepro</c>; run the rest of the suite without them with
/// <c>dotnet test --filter "Category!=DefectRepro"</c>.
/// </summary>
[Trait("Category", "DefectRepro")]
public sealed class DefectReproductionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tokonomy-defects", Guid.NewGuid().ToString("N"));

    public DefectReproductionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // D4 (review truncation) and the D10 gate-duration test live in the E2E project: they go through the orchestrator,
    // so a correct fix (multi-call review, a different property name) is judged by behaviour, not by one method.

    // ------------------------------------------------------------------------------------------------ D5

    [Theory]
    [Trait("Defect", "D5")]
    [InlineData("claude-code")]
    [InlineData("claude-code-sub")]
    public void D5_shipped_claude_worker_commands_do_not_load_the_target_repositorys_own_settings_hooks_or_mcp_servers(string executor)
    {
        // Claude Code in -p mode loads the project's .claude/settings.json (hooks) and .mcp.json without a trust prompt
        // unless told not to. The architect provider isolates this (ClaudeCliProvider: --setting-sources "" and
        // --strict-mcp-config); the shipped worker definitions do not, so a cloned repository can run its own hooks.
        var root = Path.Combine(_root, "scaffold");
        Directory.CreateDirectory(root);
        Scaffolder.Init(root, force: false);
        var definition = ConfigLoader.Load(Path.Combine(root, ".tokonomy")).Executors.Executors[executor];

        // --bare is NOT accepted as the fix: it may drop the subscription login that claude-code-sub depends on (unverified).
        // `--setting-sources "" --strict-mcp-config` blocks repo hooks and keeps the call working (live-checked on 2.1.316,
        // including a CLAUDE.md brief and acceptEdits writes). Both the scaffolded YAML and the command line ConfigExecutor
        // really builds must carry it; the second is what protects an executors.yaml written before the fix.
        AssertIsolated(executor, [.. definition.Cmd]);

        var vars = new Dictionary<string, string> { ["prompt"] = "p", ["model"] = "haiku", ["workdir"] = root };
        AssertIsolated(executor + " (built)", ConfigExecutor.IsolateWorkerSettings(definition, ConfigExecutor.BuildArgs(definition, vars, "haiku")));

        var legacy = new Tokonomy.Core.Config.ExecutorDefinition { Cmd = ["claude", "-p", "{prompt}", "--output-format", "json"] };
        AssertIsolated("legacy executors.yaml (built)", ConfigExecutor.IsolateWorkerSettings(legacy, ConfigExecutor.BuildArgs(legacy, vars, null)));
    }

    private static void AssertIsolated(string label, List<string> cmd)
    {
        var shown = string.Join(' ', cmd);
        var at = cmd.IndexOf("--setting-sources");
        Assert.True(at >= 0 && at + 1 < cmd.Count, $"{label} loads project settings and hooks: {shown}");
        var sources = cmd[at + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.DoesNotContain("project", sources);
        Assert.DoesNotContain("local", sources);
        Assert.True(cmd.Contains("--strict-mcp-config"), $"{label} loads project MCP servers: {shown}");
        Assert.DoesNotContain("--bare", cmd);
    }

    // ------------------------------------------------------------------------------------------------ D6

    [Fact]
    [Trait("Defect", "D6")]
    public void D6_the_locked_region_check_does_not_read_through_a_link_that_leaves_the_worktree()
    {
        // threat-model T17 says locked-region reads go through the worktree guard. The gate uses File.ReadAllText instead,
        // so a link planted in the worktree (a junction on Windows, a symlink elsewhere) satisfies the lock with outside text.
        var work = Path.Combine(_root, "work");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "locked.txt"), "THE LOCKED LINES\n");
        FsLinks.Directory(Path.Combine(work, "link"), outside);

        // Either outcome is a correct fix: the gate fails, or the guarded read refuses the escape with an exception. Note that
        // RunGatesAsync only catches GateSecurityException, so a raw WorktreeEscapeException would end a real run as Error;
        // the fix should turn it into a failed gate or a GateSecurityException.
        GateResult? result = null;
        var error = Record.Exception(() => result = LockedRegionGate.Evaluate(work, [new LockedRegion("link/locked.txt", "THE LOCKED LINES")]));

        Assert.True(error is not null || result!.Status == GateStatus.Failed, "the locked-region check read the locked text through a link that leaves the worktree");
    }

    // ------------------------------------------------------------------------------------------------ D7

    [Fact]
    [Trait("Defect", "D7")]
    public void D7_a_brief_that_asks_for_a_report_does_not_also_say_the_report_is_not_used()
    {
        var unit = new WorkUnit { Id = "U1", Title = "Feature", Spec = "Add the feature" };
        var brief = BriefWriter.Compile(new BriefInput
        {
            Plan = new Contract { Task = "t", Units = [unit] },
            Unit = unit,
            Attempt = 1,
            BriefFileName = "CLAUDE.md",
            RequestReport = true,
        });

        Assert.Contains("tokonomy-report.json", brief.Body, StringComparison.Ordinal);

        // Any sentence saying the report is "not used" contradicts the request, whatever the exact wording.
        var contradicting = brief.Body.Split('\n').Where(l => l.Contains("report", StringComparison.OrdinalIgnoreCase) && l.Contains("not used", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(contradicting.Count == 0, $"the brief requests a report but says it is not used: {string.Join(" | ", contradicting)}");
    }
}
