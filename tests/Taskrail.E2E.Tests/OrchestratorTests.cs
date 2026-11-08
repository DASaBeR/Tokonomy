using Taskrail.Core.Config;
using Taskrail.Core.Model;
using Taskrail.Core.Supervision;
using Taskrail.Runtime.Ledger;
using Taskrail.Runtime.Orchestration;
using Taskrail.Runtime.Process;

namespace Taskrail.E2E.Tests;

public sealed class OrchestratorTests : IDisposable
{
    // The base commit passes the gate, so a regression on it blocks (a gate red at baseline would be pre-existing).
    private readonly TestRepo _repo = new(("app.txt", "feature v1\n"), ("README.md", "# demo\n"));
    private readonly string _config;
    private readonly SqliteLedger _ledger;

    public OrchestratorTests()
    {
        _config = Path.Combine(_repo.Path, ".taskrail");
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

    private async Task<RunOutcome> Run(Contract contract, Policy policy, FakeExecutor executor, Strategy strategy, string runId = "run-1")
    {
        _ledger.StartRun(new RunRecord { Id = runId, Strategy = strategy.Id, TaskId = "t", Repo = _repo.Path, Status = RunStatus.Planned, Started = DateTimeOffset.UtcNow });
        var orchestrator = new RunOrchestrator(new ProcessRunner(), _ledger, new Supervisor(), _ => executor);
        return await orchestrator.RunAsync(new RunRequest
        {
            RunId = runId,
            Contract = contract,
            RepoRoot = _repo.Path,
            BaseSha = _repo.Head,
            Policy = policy,
            ExecutorName = "fake",
            Executor = new ExecutorDefinition { Cmd = ["fake"], Capabilities = { Headless = true, ReportsTokens = true } },
            Artifacts = new RunArtifacts(_config, runId),
            WorkerModel = strategy.WorkerIsStrong ? policy.Models.Architect : policy.Models.Worker,
            UseGates = strategy.UseGates,
            AllowEscalation = strategy.AllowEscalation,
            MaxAttempts = strategy.MaxAttempts,
        }, null, default);
    }

    [Fact]
    public async Task Supervised_run_retries_with_gate_feedback_then_commits_each_unit()
    {
        var executor = new FakeExecutor(
            FakeExecutor.Write("app.txt", "version 2\n"),            // U1 attempt 1: gate fails
            FakeExecutor.Write("app.txt", "version 2 feature\n"),    // U1 attempt 2: passes
            FakeExecutor.Write("DOCS.md", "feature docs\n"));         // U2: passes

        var outcome = await Run(TwoUnits(), PolicyWithGate(), executor, Strategy.Supervised);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(["accepted", "accepted"], outcome.Units.Select(u => u.Status));
        Assert.Equal(2, outcome.Units[0].Attempts);

        // Second attempt's brief carries the gate failure; U2's brief lists U1 as done.
        Assert.Contains("missing feature", executor.Calls[1].Brief.Body, StringComparison.Ordinal);
        Assert.Contains("U1: Feature", executor.Calls[2].Brief.Body, StringComparison.Ordinal);
        Assert.All(executor.Calls, c => Assert.Equal("cheap", c.Model));

        var log = _repo.Git("log", "--format=%s", outcome.Branch);
        Assert.StartsWith("taskrail(U2): Docs\ntaskrail(U1): Feature\ninitial", log, StringComparison.Ordinal);
        Assert.Equal("version 2 feature\n", _repo.Git("show", $"{outcome.Branch}:app.txt"));
        Assert.DoesNotContain(_repo.Git("worktree", "list", "--porcelain").Split('\n'), l => l.Contains("run-1", StringComparison.Ordinal));
        Assert.Equal(RunStatus.Pass, _ledger.GetRun("run-1")!.Status);
        Assert.True(File.Exists(Path.Combine(_config, "runs", "run-1", "transcript.jsonl")));
    }

    [Fact]
    public async Task Repeated_gate_failure_fails_the_unit_and_leaves_no_commit()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "still wrong\n"));

        var outcome = await Run(TwoUnits(), PolicyWithGate(), executor, Strategy.Supervised);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Equal(2, executor.Calls.Count);
        Assert.Contains("lands in M2", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(_repo.Head, outcome.HeadSha);
    }

    [Fact]
    public async Task Gate_failing_on_base_commit_does_not_block()
    {
        var policy = PolicyWithGate();
        policy.Gates.Add(new GateConfig { Name = "always-red", Cmd = Shell.Cmd("exit 1", "exit 1") });
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature\n"));
        var contract = new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "F", Spec = "s" }] };

        var outcome = await Run(contract, policy, executor, Strategy.Supervised);

        Assert.Equal(RunStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task Single_shot_strategy_ignores_gates_and_uses_strong_model()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "nothing\n"));

        var outcome = await Run(Strategy.SingleUnit("do it"), PolicyWithGate(), executor, Strategy.StrongOnly);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal("strong", Assert.Single(executor.Calls).Model);
    }

    [Fact]
    public async Task Protected_path_fails_when_strategy_has_no_architect()
    {
        var policy = PolicyWithGate();
        policy.Escalation.ProtectedPaths = ["secrets/**"];
        var executor = new FakeExecutor((_, dir) =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "secrets"));
            File.WriteAllText(Path.Combine(dir, "secrets", "k.txt"), "x");
            File.WriteAllText(Path.Combine(dir, "app.txt"), "feature\n");
        });

        var outcome = await Run(Strategy.SingleUnit("t"), policy, executor, Strategy.StrongWithGates);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains("no architect escalation", outcome.Error, StringComparison.Ordinal);
    }
}
