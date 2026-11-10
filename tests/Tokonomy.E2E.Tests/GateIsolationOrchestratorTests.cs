using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Supervision;
using Tokonomy.Runtime.Gates.Isolation;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.E2E.Tests;

/// <summary>
/// An isolated provider stand-in that records what the orchestrator asks of it. Each environment answers commands from a
/// script instead of running them, so these tests check the orchestration contract (fail closed, one fresh environment per
/// batch, snapshot contents, network policy, security failures) rather than any isolation technology.
/// </summary>
internal sealed class RecordingProvider : IGateExecutionProvider
{
    public string Name => "recording";
    public bool IsIsolated => true;
    public bool Available { get; init; } = true;
    public List<GateEnvironmentRequest> Requests { get; } = [];
    public int Destroyed { get; private set; }
    public int Commands { get; private set; }

    /// <summary>Decides each command: return a result, or throw (e.g. <see cref="GateSecurityException"/>).</summary>
    public Func<GateEnvironmentRequest, GateCommand, GateCommandResult> Answer { get; init; } = (_, _) => Pass;

    public static GateCommandResult Pass => new() { Termination = GateTermination.Completed, ExitCode = 0, Output = "ok" };

    public Task<ProviderStatus> CheckAsync(CancellationToken ct) =>
        Task.FromResult(Available ? ProviderStatus.Ok("ready") : ProviderStatus.Unavailable("Windows Sandbox is not enabled"));

    public Task<IGateEnvironment> CreateAsync(GateEnvironmentRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult<IGateEnvironment>(new Env(this, request));
    }

    private sealed class Env(RecordingProvider owner, GateEnvironmentRequest request) : IGateEnvironment
    {
        public TimeSpan StartupTime => TimeSpan.FromMilliseconds(1);

        public Task<GateCommandResult> RunAsync(GateCommand command, CancellationToken ct)
        {
            owner.Commands++;
            return Task.FromResult(owner.Answer(request, command));
        }

        public Task<string?> ExportDependenciesAsync(string destination, CancellationToken ct)
        {
            File.WriteAllText(destination, "archive");
            return Task.FromResult<string?>(destination);
        }

        public Task<string?> DestroyAsync(bool failed = false)
        {
            owner.Destroyed++;
            return Task.FromResult<string?>(null);
        }

        public ValueTask DisposeAsync() => new(DestroyAsync());
    }
}

public sealed class GateIsolationOrchestratorTests : IDisposable
{
    private readonly TestRepo _repo = new(("app.txt", "feature v1\n"), (".gitignore", "node_modules/\n"));
    private readonly string _config;
    private readonly SqliteLedger _ledger;

    public GateIsolationOrchestratorTests()
    {
        _config = Path.Combine(_repo.Path, ".tokonomy");
        _ledger = new SqliteLedger(Path.Combine(_config, "ledger.db"));
    }

    public void Dispose()
    {
        _ledger.Dispose();
        _repo.Dispose();
    }

    private static Policy Policy(GateExecutionConfig? gateExecution) => new()
    {
        Models = { Architect = "strong", Worker = "cheap" },
        GateExecution = gateExecution,
        Gates = [new GateConfig { Name = "test", Cmd = ["python", "-m", "pytest"] }],
    };

    private static GateExecutionConfig Sandboxed => new() { Provider = GateExecutionConfig.WindowsSandboxProvider };

    private async Task<RunOutcome> Run(FakeExecutor executor, Policy policy, RecordingProvider? provider)
    {
        _ledger.StartRun(new RunRecord { Id = "run-1", Strategy = Strategy.StrongWithGates.Id, TaskId = "t", Repo = _repo.Path, Status = RunStatus.Planned, Started = DateTimeOffset.UtcNow });
        var orchestrator = new RunOrchestrator(new ProcessRunner(), _ledger, new Supervisor(), _ => executor, gateProvider: provider);
        return await orchestrator.RunAsync(new RunRequest
        {
            RunId = "run-1",
            Contract = new Contract
            {
                Task = "Add the feature",
                Units = [new WorkUnit { Id = "U1", Title = "Feature", Spec = "Edit app.txt" }],
                Rules = [new Rule("R1", "keep it small")],
            },
            RepoRoot = _repo.Path,
            BaseSha = _repo.Head,
            Policy = policy,
            ExecutorName = "fake",
            Executor = new ExecutorDefinition { Cmd = ["fake"], Capabilities = { Headless = true, ReportsTokens = true } },
            Artifacts = new RunArtifacts(_config, "run-1"),
            WorkerModel = "cheap",
            UseGates = true,
            AllowEscalation = true,
            MaxAttempts = Strategy.StrongWithGates.MaxAttempts,
        }, null, default);
    }

    [Fact]
    public async Task Without_a_gate_execution_section_the_run_stops_before_any_worktree_or_worker()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"));

        var outcome = await Run(executor, Policy(null), provider: null);

        Assert.Equal(RunStatus.Error, outcome.Status);
        Assert.Contains("gate_execution", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(executor.Calls);
        Assert.Equal("", outcome.Branch);
        Assert.DoesNotContain("tokonomy/run-1", _repo.Git("branch", "--list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unavailable_provider_stops_the_run_and_never_falls_back_to_the_host()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"));
        var provider = new RecordingProvider { Available = false };

        var outcome = await Run(executor, Policy(Sandboxed), provider);

        Assert.Equal(RunStatus.Error, outcome.Status);
        Assert.Contains("not enabled", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(executor.Calls);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Every_gate_batch_gets_a_fresh_environment_that_is_destroyed_afterwards()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"));
        var provider = new RecordingProvider();

        var outcome = await Run(executor, Policy(Sandboxed), provider);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(["baseline", "U1-a1"], provider.Requests.Select(r => r.Label));
        Assert.Equal(provider.Requests.Count, provider.Destroyed);
        Assert.All(provider.Requests, r => Assert.Equal(NetworkAccess.None, r.Network));
    }

    [Fact]
    public async Task The_environment_receives_the_attempts_files_but_not_git_metadata_tokonomy_state_or_ignored_content()
    {
        var executor = new FakeExecutor((_, wt) =>
        {
            File.WriteAllText(Path.Combine(wt, "app.txt"), "feature v2\n");
            File.WriteAllText(Path.Combine(wt, "new_test.py"), "assert True\n");
            Directory.CreateDirectory(Path.Combine(wt, "node_modules"));
            File.WriteAllText(Path.Combine(wt, "node_modules", "hidden.js"), "steal()");
        });
        var provider = new RecordingProvider();

        await Run(executor, Policy(Sandboxed), provider);

        var attempt = provider.Requests.Single(r => r.Label == "U1-a1").Source.Files;
        Assert.Contains("new_test.py", attempt);
        Assert.Contains("app.txt", attempt);
        Assert.DoesNotContain(attempt, f => f.StartsWith(".git/", StringComparison.Ordinal) || f == ".git");
        Assert.DoesNotContain(attempt, f => f.StartsWith(".tokonomy/", StringComparison.Ordinal));
        Assert.DoesNotContain(attempt, f => f.StartsWith("node_modules/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_security_violation_in_the_gate_environment_fails_the_unit_at_once_and_is_never_retried()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"));
        var provider = new RecordingProvider
        {
            Answer = (request, _) => request.Label == "baseline"
                ? RecordingProvider.Pass
                : throw new GateSecurityException("the gate environment created link(s) in its results folder (0.report -> C:\\Users)"),
        };

        var outcome = await Run(executor, Policy(Sandboxed), provider);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        var unit = Assert.Single(outcome.Units);
        Assert.Contains("workspace violation: gate environment", unit.Reason, StringComparison.Ordinal);
        Assert.Single(executor.Calls);   // not retried, not taken over
        Assert.Equal(provider.Requests.Count, provider.Destroyed);
    }

    [Fact]
    public async Task A_protocol_violation_counts_as_a_failed_gate_not_a_pass()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"));
        var provider = new RecordingProvider
        {
            Answer = (request, _) => request.Label == "baseline"
                ? RecordingProvider.Pass
                : new GateCommandResult { Termination = GateTermination.ProtocolViolation, ExitCode = 0, Output = "no valid status" },
        };

        var outcome = await Run(executor, Policy(Sandboxed), provider);

        Assert.NotEqual(RunStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task Prepare_runs_in_its_own_environment_with_the_prepare_network_and_gates_get_its_archive_without_network()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"));
        var provider = new RecordingProvider();
        var policy = Policy(Sandboxed);
        policy.Prepare = [new GateConfig { Name = "deps", Cmd = ["npm", "ci"] }];

        var outcome = await Run(executor, policy, provider);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        var prepare = provider.Requests[0];
        Assert.Equal("prepare", prepare.Label);
        Assert.Equal(NetworkAccess.Full, prepare.Network);
        Assert.Null(prepare.Dependencies);
        Assert.All(provider.Requests.Skip(1), r =>
        {
            Assert.Equal(NetworkAccess.None, r.Network);
            Assert.EndsWith("deps.tar", r.Dependencies, StringComparison.Ordinal);
        });
        Assert.False(File.Exists(provider.Requests[1].Dependencies));   // the archive is deleted with the run
    }

    [Fact]
    public async Task Prepare_without_network_is_honoured()
    {
        var provider = new RecordingProvider();
        var policy = Policy(new GateExecutionConfig { Provider = GateExecutionConfig.WindowsSandboxProvider, PrepareNetwork = "none" });
        policy.Prepare = [new GateConfig { Name = "deps", Cmd = ["npm", "ci", "--offline"] }];

        await Run(new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n")), policy, provider);

        Assert.Equal(NetworkAccess.None, provider.Requests[0].Network);
    }

    [Fact]
    public async Task A_strategy_without_gates_needs_no_provider_at_all()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"));
        _ledger.StartRun(new RunRecord { Id = "run-1", Strategy = Strategy.StrongWithGates.Id, TaskId = "t", Repo = _repo.Path, Status = RunStatus.Planned, Started = DateTimeOffset.UtcNow });
        var orchestrator = new RunOrchestrator(new ProcessRunner(), _ledger, new Supervisor(), _ => executor);

        var outcome = await orchestrator.RunAsync(new RunRequest
        {
            RunId = "run-1",
            Contract = new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "F", Spec = "s" }] },
            RepoRoot = _repo.Path,
            BaseSha = _repo.Head,
            Policy = Policy(null),
            ExecutorName = "fake",
            Executor = new ExecutorDefinition { Cmd = ["fake"], Capabilities = { Headless = true, ReportsTokens = true } },
            Artifacts = new RunArtifacts(_config, "run-1"),
            UseGates = false,
            AllowEscalation = false,
        }, null, default);

        Assert.Equal(RunStatus.Pass, outcome.Status);
    }
}
