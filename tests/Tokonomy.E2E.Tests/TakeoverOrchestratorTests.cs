using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Supervision;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.E2E.Tests;

/// <summary>The whole takeover loop with real git, gates and ledger; only the worker and the architect are scripted.</summary>
public sealed class TakeoverOrchestratorTests : IDisposable
{
    // The base commit passes the gate, so a regression on it blocks.
    private readonly TestRepo _repo = new(("app.txt", "feature v1\n"), ("README.md", "# demo\n"));
    private readonly string _config;
    private readonly SqliteLedger _ledger;

    public TakeoverOrchestratorTests()
    {
        _config = Path.Combine(_repo.Path, ".tokonomy");
        _ledger = new SqliteLedger(Path.Combine(_config, "ledger.db"));
    }

    private string RunDir => Path.Combine(_config, "runs", "run-1");

    public void Dispose()
    {
        _ledger.Dispose();
        _repo.Dispose();
    }

    private static Policy PolicyWithGate() => new()
    {
        Models = { Architect = "strong", Worker = "cheap" },
        Gates = [new GateConfig { Name = "has-feature", Cmd = Shell.FileContains("app.txt", "feature") }],
    };

    private static Contract TwoUnits() => new()
    {
        Task = "Add the feature and docs",
        Units =
        [
            new WorkUnit { Id = "U1", Title = "Feature", Spec = "Add 'feature' to app.txt" },
            new WorkUnit { Id = "U2", Title = "Docs", Spec = "Document it", DependsOn = ["U1"] },
        ],
        Rules = [new Rule("R1", "keep it small")],
    };

    private async Task<RunOutcome> Run(Contract contract, Policy policy, FakeExecutor executor, Strategy strategy, IProvider? architect)
    {
        _ledger.StartRun(new RunRecord { Id = "run-1", Strategy = strategy.Id, TaskId = "t", Repo = _repo.Path, Status = RunStatus.Planned, Started = DateTimeOffset.UtcNow });
        var orchestrator = new RunOrchestrator(new ProcessRunner(), _ledger, new Supervisor(), _ => executor, architect: architect);
        return await orchestrator.RunAsync(new RunRequest
        {
            RunId = "run-1",
            Contract = contract,
            RepoRoot = _repo.Path,
            BaseSha = _repo.Head,
            Policy = policy,
            ExecutorName = "fake",
            Executor = new ExecutorDefinition { Cmd = ["fake"], Capabilities = { Headless = true, ReportsTokens = true } },
            Artifacts = new RunArtifacts(_config, "run-1"),
            WorkerModel = strategy.WorkerIsStrong ? policy.Models.Architect : policy.Models.Worker,
            UseGates = strategy.UseGates,
            AllowEscalation = strategy.AllowEscalation,
            MaxAttempts = strategy.MaxAttempts,
        }, null, default);
    }

    [Fact]
    public async Task Takeover_fixes_the_unit_after_two_failed_attempts_and_hands_off_to_the_next_unit()
    {
        var executor = new FakeExecutor(
            FakeExecutor.Write("app.txt", "nothing\n"),     // U1 attempt 1: gate fails
            FakeExecutor.Write("app.txt", "nothing\n"),     // U1 attempt 2: gate fails again -> takeover
            FakeExecutor.Write("DOCS.md", "docs\n"));       // U2
        var architect = new FakeArchitect(FakeArchitect.Edit("app.txt", "nothing", "feature fixed"));

        var outcome = await Run(TwoUnits(), PolicyWithGate(), executor, Strategy.Supervised, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(["accepted", "accepted"], outcome.Units.Select(u => u.Status));
        Assert.StartsWith("architect takeover", outcome.Units[0].Reason, StringComparison.Ordinal);
        Assert.Equal(2, executor.Calls.Count(c => c.Brief.Label.StartsWith("U1", StringComparison.Ordinal)));

        // The architect saw the failure, the worker's diff and the file text, and was charged to the run.
        var request = Assert.Single(architect.Requests);
        Assert.Contains("missing feature", request.User, StringComparison.Ordinal);
        Assert.Contains("nothing", request.User, StringComparison.Ordinal);
        Assert.Equal(0.02m, outcome.TotalUsd);
        Assert.Equal(1, _ledger.GetSummary("run-1")!.Tiers.Single(t => t.Role == LlmRole.Architect).Calls);

        // One commit per unit; the takeover commit is labelled; the fix is on the branch.
        var subjects = _repo.Git("log", "--format=%s", outcome.Branch);
        Assert.StartsWith("tokonomy(U2): Docs\ntokonomy(U1): Feature [architect takeover]\ninitial", subjects, StringComparison.Ordinal);
        Assert.Equal("feature fixed\n", _repo.Git("show", $"{outcome.Branch}:app.txt"));

        // Handoff file and the next worker's brief.
        var handoff = File.ReadAllText(Path.Combine(RunDir, "handoff.yaml"));
        Assert.Contains("Fixed it", handoff, StringComparison.Ordinal);
        Assert.Contains("The worker got it wrong", handoff, StringComparison.Ordinal);
        Assert.Contains("U2: Docs", handoff, StringComparison.Ordinal);
        var u2Brief = executor.Calls.Last().Brief.Body;
        Assert.Contains("## Handoff from the architect", u2Brief, StringComparison.Ordinal);
        Assert.Contains("feature fixed", u2Brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locked_region_written_by_the_architect_is_enforced_on_later_units()
    {
        var executor = new FakeExecutor(
            FakeExecutor.Write("app.txt", "nothing\n"),
            FakeExecutor.Write("app.txt", "nothing\n"),
            // U2 attempt 1 edits the locked line (the visible gate still passes because "feature" is present)
            (_, dir) =>
            {
                File.WriteAllText(Path.Combine(dir, "app.txt"), "feature changed\n");
                File.WriteAllText(Path.Combine(dir, "DOCS.md"), "docs\n");
            },
            // U2 attempt 2 restores it
            FakeExecutor.Write("app.txt", "feature fixed\n"));
        var architect = new FakeArchitect(FakeArchitect.Edit("app.txt", "nothing", "feature fixed"));

        var outcome = await Run(TwoUnits(), PolicyWithGate(), executor, Strategy.Supervised, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(2, outcome.Units[1].Attempts);
        var retryBrief = executor.Calls.Last().Brief.Body;
        Assert.Contains("Previous attempt failed", retryBrief, StringComparison.Ordinal);
        Assert.Contains("Locked regions were changed or removed in: app.txt", retryBrief, StringComparison.Ordinal);
        Assert.Equal("feature fixed\n", _repo.Git("show", $"{outcome.Branch}:app.txt"));
    }

    [Fact]
    public async Task Edits_that_cannot_be_applied_fail_the_unit_after_one_reask_and_leave_the_branch_clean()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "nothing\n"));
        var architect = new FakeArchitect("Sorry, I cannot fix this.");

        var outcome = await Run(TwoUnits(), PolicyWithGate(), executor, Strategy.Supervised, architect);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains("takeover failed", outcome.Error, StringComparison.Ordinal);
        Assert.Contains("could not be applied", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(2, architect.Requests.Count);
        Assert.Equal(2, _ledger.GetSummary("run-1")!.Tiers.Single(t => t.Role == LlmRole.Architect).Calls);
        Assert.Equal(_repo.Head, outcome.HeadSha);
    }

    [Fact]
    public async Task The_reask_carries_the_applier_error_and_can_succeed()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "nothing\n"), FakeExecutor.Write("app.txt", "nothing\n"), FakeExecutor.Write("DOCS.md", "d\n"));
        var architect = new FakeArchitect(
            FakeArchitect.Edit("app.txt", "text that is not in the file", "feature"),
            FakeArchitect.Edit("app.txt", "nothing", "feature after reask"));

        var outcome = await Run(TwoUnits(), PolicyWithGate(), executor, Strategy.Supervised, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(2, architect.Requests.Count);
        Assert.Contains("SEARCH text was not found in the file", architect.Requests[1].User, StringComparison.Ordinal);
        Assert.Equal("feature after reask\n", _repo.Git("show", $"{outcome.Branch}:app.txt"));
    }

    [Fact]
    public async Task An_edit_that_still_fails_the_gates_fails_the_unit()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "nothing\n"));
        var architect = new FakeArchitect(FakeArchitect.Edit("app.txt", "nothing", "still nothing"));

        var outcome = await Run(TwoUnits(), PolicyWithGate(), executor, Strategy.Supervised, architect);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains("gate(s) still failing after the architect's edit: has-feature", outcome.Error, StringComparison.Ordinal);
        Assert.Single(architect.Requests);
        Assert.Equal(_repo.Head, outcome.HeadSha);
    }

    [Fact]
    public async Task Protected_paths_need_a_human_even_when_an_architect_is_available()
    {
        var policy = PolicyWithGate();
        policy.Escalation.ProtectedPaths = ["secrets/**"];
        var executor = new FakeExecutor((_, dir) =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "secrets"));
            File.WriteAllText(Path.Combine(dir, "secrets", "k.txt"), "x");
            File.WriteAllText(Path.Combine(dir, "app.txt"), "feature\n");
        });
        var architect = new FakeArchitect(FakeArchitect.Edit("app.txt", "x", "y"));

        var outcome = await Run(Strategy.SingleUnit("t"), policy, executor, Strategy.Supervised, architect);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains("needs a human decision", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(architect.Requests);
    }

    [Fact]
    public async Task Takeover_can_be_switched_off_per_unit()
    {
        var policy = PolicyWithGate();
        policy.Escalation.MaxTakeoversPerUnit = 0;
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "nothing\n"));
        var architect = new FakeArchitect(FakeArchitect.Edit("app.txt", "nothing", "feature"));

        var outcome = await Run(TwoUnits(), policy, executor, Strategy.Supervised, architect);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains("takeover unavailable (takeover limit reached for this unit)", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(architect.Requests);
    }

    [Fact]
    public async Task A_worker_that_changes_nothing_can_be_rescued_with_the_unit_files_in_the_prompt()
    {
        var executor = new FakeExecutor((_, _) => { });   // never touches anything
        var architect = new FakeArchitect(FakeArchitect.Edit("app.txt", "feature v1", "feature v2"));
        var contract = new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "Edit", Spec = "edit app.txt", Files = ["app.txt"] }] };

        var outcome = await Run(contract, PolicyWithGate(), executor, Strategy.Supervised, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        var prompt = Assert.Single(architect.Requests).User;
        Assert.Contains("(no changes were made)", prompt, StringComparison.Ordinal);
        Assert.Contains("feature v1", prompt, StringComparison.Ordinal);   // it was shown the unit's file
        Assert.Equal("feature v2\n", _repo.Git("show", $"{outcome.Branch}:app.txt"));
    }

    [Fact]
    public async Task Strategies_without_escalation_never_call_the_architect()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "nothing\n"));
        var architect = new FakeArchitect(FakeArchitect.Edit("app.txt", "nothing", "feature"));

        var outcome = await Run(Strategy.SingleUnit("t"), PolicyWithGate(), executor, Strategy.StrongWithGates, architect);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains("no architect escalation in this strategy", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(architect.Requests);
    }
}
