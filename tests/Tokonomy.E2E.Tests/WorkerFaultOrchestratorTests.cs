using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Supervision;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.E2E.Tests;

/// <summary>
/// A worker run that faulted (denied tool call, crash, wrong model) must never be reported as a successful
/// implementation, even when its diff passes every gate. Real git, gates and ledger; the worker and architect are scripted.
/// </summary>
public sealed class WorkerFaultOrchestratorTests : IDisposable
{
    private static readonly WorkerFault Denied = new(WorkerFaultKind.PermissionDenied, "command(s) outside the allowlist: python -c ...");

    private readonly TestRepo _repo = new(("app.txt", "feature v1\n"));
    private readonly string _config;
    private readonly SqliteLedger _ledger;

    public WorkerFaultOrchestratorTests()
    {
        _config = Path.Combine(_repo.Path, ".tokonomy");
        _ledger = new SqliteLedger(Path.Combine(_config, "ledger.db"));
    }

    public void Dispose()
    {
        _ledger.Dispose();
        _repo.Dispose();
    }

    private static Policy PolicyWithGate() => new()
    {
        Models = { Architect = "strong", Worker = "cheap" },
        GateExecution = new GateExecutionConfig { Provider = GateExecutionConfig.HostUnsafeProvider },
        Gates = [new GateConfig { Name = "has-feature", Cmd = Shell.FileContains("app.txt", "feature") }],
    };

    private static Contract OneUnit() => new()
    {
        Task = "Add the feature",
        Units = [new WorkUnit { Id = "U1", Title = "Feature", Spec = "Edit app.txt" }],
        Rules = [new Rule("R1", "keep it small")],
    };

    private async Task<RunOutcome> Run(FakeExecutor executor, Strategy strategy, IProvider? architect, Policy? policy = null)
    {
        policy ??= PolicyWithGate();
        _ledger.StartRun(new RunRecord { Id = "run-1", Strategy = strategy.Id, TaskId = "t", Repo = _repo.Path, Status = RunStatus.Planned, Started = DateTimeOffset.UtcNow });
        var orchestrator = new RunOrchestrator(new ProcessRunner(), _ledger, new Supervisor(), _ => executor, architect: architect);
        return await orchestrator.RunAsync(new RunRequest
        {
            RunId = "run-1",
            Contract = OneUnit(),
            RepoRoot = _repo.Path,
            BaseSha = _repo.Head,
            Policy = policy,
            ExecutorName = "fake",
            Executor = new ExecutorDefinition { Cmd = ["fake"], Capabilities = { Headless = true, ReportsTokens = true } },
            Artifacts = new RunArtifacts(_config, "run-1"),
            WorkerModel = "cheap",
            UseGates = strategy.UseGates,
            AllowEscalation = strategy.AllowEscalation,
            MaxAttempts = strategy.MaxAttempts,
        }, null, default);
    }

    [Fact]
    public async Task A_faulted_worker_is_not_accepted_even_when_its_diff_passes_the_gates()
    {
        // The edit is perfect (gate passes) but every run reports a denied command: the session ended early.
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n")) { FaultFor = _ => Denied };

        var outcome = await Run(executor, Strategy.StrongWithGates, architect: null);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        var unit = Assert.Single(outcome.Units);
        Assert.Equal("failed", unit.Status);
        Assert.Contains("PermissionDenied", unit.Reason, StringComparison.Ordinal);
        Assert.Equal(2, executor.Calls.Count);   // retried up to the limit, never accepted

        // Nothing from the faulted worker reached the run branch.
        Assert.Equal("feature v1\n", _repo.Git("show", $"{outcome.Branch}:app.txt"));
    }

    [Fact]
    public async Task The_retry_brief_tells_the_worker_what_was_denied()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n")) { FaultFor = _ => Denied };

        await Run(executor, Strategy.StrongWithGates, architect: null);

        Assert.Contains("command(s) outside the allowlist", executor.Calls[1].Brief.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Takeover_is_explicit_and_recorded_when_the_worker_keeps_faulting()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "nothing\n")) { FaultFor = _ => Denied };
        var architect = new FakeArchitect(FakeArchitect.Edit("app.txt", "nothing", "feature by the architect"));

        var outcome = await Run(executor, Strategy.Supervised, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        var unit = Assert.Single(outcome.Units);
        Assert.StartsWith("architect takeover", unit.Reason, StringComparison.Ordinal);
        Assert.Single(architect.Requests);
        Assert.Contains("PermissionDenied", architect.Requests[0].User, StringComparison.Ordinal);
        Assert.Equal("feature by the architect\n", _repo.Git("show", $"{outcome.Branch}:app.txt"));
        Assert.Contains("[architect takeover]", _repo.Git("log", "--format=%s", outcome.Branch), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_mismatch_fails_at_once_without_retry_or_takeover()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"))
        {
            FaultFor = _ => new WorkerFault(WorkerFaultKind.ModelMismatch, "requested 'gemini-3.8-flash-medium' but the run used 'claude-opus-4-6-thinking'"),
        };
        var architect = new FakeArchitect(FakeArchitect.Edit("app.txt", "feature v2", "feature v3"));

        var outcome = await Run(executor, Strategy.Supervised, architect);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains("wrong model", Assert.Single(outcome.Units).Reason, StringComparison.Ordinal);
        Assert.Single(executor.Calls);          // not retried
        Assert.Empty(architect.Requests);       // and the architect did not quietly do the work instead
        Assert.Equal("feature v1\n", _repo.Git("show", $"{outcome.Branch}:app.txt"));
    }

    private static Policy ReviewPolicy()
    {
        var policy = PolicyWithGate();
        policy.Escalation.FinalReview = true;
        return policy;
    }

    private sealed class ThrowingArchitect : IProvider
    {
        public Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("claude is down");
    }

    [Fact]
    public async Task Final_review_runs_once_after_every_unit_passed_and_is_recorded()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"));
        var architect = new FakeArchitect("VERDICT: APPROVE\nSUMMARY: The change does what the task asked.\n");

        var outcome = await Run(executor, Strategy.Supervised, architect, ReviewPolicy());

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(ReviewVerdict.Approve, outcome.Review?.Verdict);
        var request = Assert.Single(architect.Requests);
        Assert.Contains("feature v2", request.User, StringComparison.Ordinal);   // it saw the combined diff
        Assert.Contains("U1 Feature: accepted", request.User, StringComparison.Ordinal);
        Assert.Contains("\"verdict\":\"Approve\"", File.ReadAllText(Path.Combine(_config, "runs", "run-1", "transcript.jsonl")).Replace(" ", ""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_concerns_are_advisory_and_do_not_fail_a_run_the_gates_passed()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"));
        var architect = new FakeArchitect("VERDICT: CONCERNS\nSUMMARY: Unrelated edit.\nCONCERNS:\n- app.txt: changed more than asked\n");

        var outcome = await Run(executor, Strategy.Supervised, architect, ReviewPolicy());

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(ReviewVerdict.Concerns, outcome.Review?.Verdict);
        Assert.Equal(["app.txt: changed more than asked"], outcome.Review!.Concerns);
    }

    [Fact]
    public async Task An_unavailable_architect_does_not_fail_the_run_and_the_review_says_so()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"));

        var outcome = await Run(executor, Strategy.Supervised, new ThrowingArchitect(), ReviewPolicy());

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(ReviewVerdict.Unavailable, outcome.Review?.Verdict);
        Assert.Contains("claude is down", outcome.Review!.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_review_when_it_is_off_the_run_failed_or_there_is_no_architect()
    {
        var architect = new FakeArchitect("VERDICT: APPROVE\nSUMMARY: x");

        var off = await Run(new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n")), Strategy.Supervised, architect);
        Assert.Null(off.Review);
        Assert.Empty(architect.Requests);
    }

    [Fact]
    public async Task A_failed_run_is_not_reviewed()
    {
        var architect = new FakeArchitect("VERDICT: APPROVE\nSUMMARY: x");
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n")) { FaultFor = _ => new WorkerFault(WorkerFaultKind.ModelMismatch, "wrong") };

        var outcome = await Run(executor, Strategy.Supervised, architect, ReviewPolicy());

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Null(outcome.Review);
        Assert.Empty(architect.Requests);
    }

    [Fact]
    public async Task A_transient_fault_followed_by_a_clean_run_is_accepted()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"))
        {
            FaultFor = call => call == 1 ? new WorkerFault(WorkerFaultKind.ProcessFailed, "exit code 1") : null,
        };

        var outcome = await Run(executor, Strategy.StrongWithGates, architect: null);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(2, executor.Calls.Count);
        Assert.Equal("feature v2\n", _repo.Git("show", $"{outcome.Branch}:app.txt"));
    }
}
