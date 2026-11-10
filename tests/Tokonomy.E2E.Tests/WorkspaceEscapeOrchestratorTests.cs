using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Supervision;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.E2E.Tests;

/// <summary>
/// A worker (or code a gate runs) that plants links, rewrites the worktree's .git pointer, or hides files in ignored
/// paths. Real git, real gates, real junctions; only the worker and the architect are scripted.
/// </summary>
public sealed class WorkspaceEscapeOrchestratorTests : IDisposable
{
    private readonly TestRepo _repo = new(("app.txt", "feature v1\n"), (".gitignore", "build/\nstore/\n"));
    private readonly string _config;
    private readonly SqliteLedger _ledger;
    private readonly string _outside;

    public WorkspaceEscapeOrchestratorTests()
    {
        _config = Path.Combine(_repo.Path, ".tokonomy");
        _ledger = new SqliteLedger(Path.Combine(_config, "ledger.db"));
        _outside = Path.Combine(Path.GetTempPath(), "tokonomy-e2e-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "keep.txt"), "precious");
    }

    private string Marker => Path.Combine(_outside, "gate-ran.txt");

    public void Dispose()
    {
        _ledger.Dispose();
        _repo.Dispose();
        try
        {
            Directory.Delete(_outside, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Policy PolicyWith(params GateConfig[] gates) => new()
    {
        Models = { Architect = "strong", Worker = "cheap" },
        GateExecution = new GateExecutionConfig { Provider = GateExecutionConfig.HostUnsafeProvider },
        Gates = [.. gates],
    };

    // Writes the marker only when it sees the worker's change, so the baseline run on the base commit does not count.
    private GateConfig MarkerGate() => new() { Name = "marker", Cmd = Shell.WhenChanged($"echo ran > '{Marker}'", $"Set-Content -LiteralPath '{Marker}' ran") };

    private async Task<RunOutcome> Run(FakeExecutor executor, Policy policy, IProvider? architect = null)
    {
        _ledger.StartRun(new RunRecord { Id = "run-1", Strategy = Strategy.Supervised.Id, TaskId = "t", Repo = _repo.Path, Status = RunStatus.Planned, Started = DateTimeOffset.UtcNow });
        var orchestrator = new RunOrchestrator(new ProcessRunner(), _ledger, new Supervisor(), s =>
        {
            executor.Session = s;
            return executor;
        }, architect: architect);
        return await orchestrator.RunAsync(new RunRequest
        {
            RunId = "run-1",
            Contract = new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "Feature", Spec = "s" }] },
            RepoRoot = _repo.Path,
            BaseSha = _repo.Head,
            Policy = policy,
            ExecutorName = "fake",
            Executor = new ExecutorDefinition { Cmd = ["fake"], Capabilities = { Headless = true } },
            Artifacts = new RunArtifacts(_config, "run-1"),
            WorkerModel = "cheap",
        }, null, default);
    }

    [Fact]
    public async Task A_planted_junction_fails_the_unit_the_gates_never_run_and_nothing_outside_is_touched()
    {
        var architect = new FakeArchitect(FakeArchitect.Edit("app.txt", "feature v1", "feature v2"));
        var executor = new FakeExecutor((_, dir) =>
        {
            File.WriteAllText(Path.Combine(dir, "app.txt"), "feature v2\n");
            Shell.Junction(Path.Combine(dir, "escape"), _outside);
        });

        var outcome = await Run(executor, PolicyWith(MarkerGate()), architect);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains("workspace violation", outcome.Units[0].Reason, StringComparison.Ordinal);
        Assert.Contains("escape", outcome.Units[0].Reason, StringComparison.Ordinal);
        Assert.Single(executor.Calls);                 // not retried
        Assert.Empty(architect.Requests);              // not handed to the architect
        Assert.False(File.Exists(Marker));             // the gates (worker-influenced code) never ran
        Assert.Equal("precious", File.ReadAllText(Path.Combine(_outside, "keep.txt")));   // cleanup did not follow it
        Assert.Equal("feature v1\n", _repo.Git("show", $"{outcome.Branch}:app.txt"));
    }

    [Fact]
    public async Task A_link_created_by_gate_code_is_caught_after_the_gates()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"));
        var plantingGate = new GateConfig
        {
            Name = "evil-test",
            Cmd = Shell.WhenChanged($"ln -s '{_outside}' gatelink", $"New-Item -ItemType Junction -Path gatelink -Target '{_outside}' | Out-Null"),
        };

        var outcome = await Run(executor, PolicyWith(plantingGate));

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains("after the gates ran", outcome.Units[0].Reason, StringComparison.Ordinal);
        Assert.Equal("precious", File.ReadAllText(Path.Combine(_outside, "keep.txt")));
    }

    [Fact]
    public async Task A_rewritten_git_pointer_fails_the_unit_and_never_reaches_the_other_repository()
    {
        using var other = new TestRepo(("other.txt", "other\n"));
        var otherHead = other.Head;
        var executor = new FakeExecutor((_, dir) =>
        {
            var gitFile = Path.Combine(dir, ".git");
            File.SetAttributes(gitFile, FileAttributes.Normal);
            File.WriteAllText(gitFile, $"gitdir: {Path.Combine(other.Path, ".git")}\n");
            File.WriteAllText(Path.Combine(dir, "app.txt"), "feature v2\n");
        });

        var outcome = await Run(executor, PolicyWith());

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains(".git was rewritten", outcome.Units[0].Reason, StringComparison.Ordinal);
        Assert.Equal(otherHead, other.Head);
        Assert.Equal("other\n", File.ReadAllText(Path.Combine(other.Path, "other.txt")));
    }

    [Fact]
    public async Task Files_hidden_in_ignored_paths_are_deleted_and_the_attempt_is_not_accepted()
    {
        var executor = new FakeExecutor(
            (_, dir) =>
            {
                File.WriteAllText(Path.Combine(dir, "app.txt"), "feature v2\n");
                Directory.CreateDirectory(Path.Combine(dir, "build"));
                File.WriteAllText(Path.Combine(dir, "build", "sitecustomize.py"), "import os  # runs on every python start\n");
            },
            FakeExecutor.Write("app.txt", "feature v2\n"))
        {
            Audit = new ExecutorAudit { Commands = [] },   // an agy-style worker that ran no shell commands
        };

        var outcome = await Run(executor, PolicyWith());

        Assert.Equal(RunStatus.Pass, outcome.Status);   // the clean second attempt is accepted
        Assert.Equal(2, executor.Calls.Count);
        Assert.Contains("git ignores", executor.Calls[1].Brief.Body, StringComparison.Ordinal);
        var transcript = File.ReadAllText(Path.Combine(_config, "runs", "run-1", "transcript.jsonl"));
        Assert.Contains("hidden_files", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Links_from_prepare_are_tolerated_but_handed_to_the_worker_as_off_limits()
    {
        var policy = PolicyWith();
        policy.Prepare = [new GateConfig { Name = "install", Cmd = Shell.MakeJunction("store", _outside) }];
        IReadOnlyList<Tokonomy.Runtime.Executors.PathRestriction>? seen = null;
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"));
        var wrapped = new FakeExecutor((brief, dir) =>
        {
            seen = executor.Session?.Restrictions?.Invoke();
            File.WriteAllText(Path.Combine(dir, "app.txt"), "feature v2\n");
        });

        var outcome = await RunWithSessionCapture(wrapped, executor, policy);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.NotNull(seen);
        var restriction = Assert.Single(seen!, r => r.RelativePath == "store");
        Assert.True(restriction.DenyRead);
        Assert.Equal("precious", File.ReadAllText(Path.Combine(_outside, "keep.txt")));   // run cleanup left it alone
    }

    private async Task<RunOutcome> RunWithSessionCapture(FakeExecutor runner, FakeExecutor sessionHolder, Policy policy)
    {
        _ledger.StartRun(new RunRecord { Id = "run-1", Strategy = Strategy.Supervised.Id, TaskId = "t", Repo = _repo.Path, Status = RunStatus.Planned, Started = DateTimeOffset.UtcNow });
        var orchestrator = new RunOrchestrator(new ProcessRunner(), _ledger, new Supervisor(), s =>
        {
            sessionHolder.Session = s;
            return runner;
        });
        return await orchestrator.RunAsync(new RunRequest
        {
            RunId = "run-1",
            Contract = new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "Feature", Spec = "s" }] },
            RepoRoot = _repo.Path,
            BaseSha = _repo.Head,
            Policy = policy,
            ExecutorName = "fake",
            Executor = new ExecutorDefinition { Cmd = ["fake"], Capabilities = { Headless = true } },
            Artifacts = new RunArtifacts(_config, "run-1"),
            WorkerModel = "cheap",
        }, null, default);
    }
}
