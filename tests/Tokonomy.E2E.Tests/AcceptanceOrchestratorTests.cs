using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Supervision;
using Tokonomy.Runtime.Gates.Isolation;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.E2E.Tests;

/// <summary>
/// The architect writes the acceptance tests, the worker cannot change them, and a unit is accepted only when they pass.
/// The provider stand-in plays the test runner: it passes only if the test file is still the architect's original and the
/// worker's change is present.
/// </summary>
public sealed class AcceptanceOrchestratorTests : IDisposable
{
    private const string Original = "ARCHITECT TEST: app.txt must say v2\n";
    private const string TestPath = "acceptance/test_app.py";

    private readonly TestRepo _repo = new(("app.txt", "feature v1\n"));
    private readonly string _config;
    private readonly SqliteLedger _ledger;

    public AcceptanceOrchestratorTests()
    {
        _config = Path.Combine(_repo.Path, ".tokonomy");
        _ledger = new SqliteLedger(Path.Combine(_config, "ledger.db"));
    }

    public void Dispose()
    {
        _ledger.Dispose();
        _repo.Dispose();
    }

    private static RecordingProvider Runner(List<string> seen) => new()
    {
        Answer = (request, command) =>
        {
            if (command.Name != "acceptance")
            {
                return RecordingProvider.Pass;
            }

            var test = File.ReadAllText(Path.Combine(request.Source.Root, TestPath));
            var app = File.ReadAllText(Path.Combine(request.Source.Root, "app.txt"));
            seen.Add(test);
            return test == Original && app.Contains("v2", StringComparison.Ordinal)
                ? RecordingProvider.Pass
                : new GateCommandResult { Termination = GateTermination.Completed, ExitCode = 1, Output = "FAILED test_app: app.txt does not say v2" };
        },
    };

    private async Task<RunOutcome> Run(FakeExecutor executor, RecordingProvider provider, int? maxAttempts = null)
    {
        _ledger.StartRun(new RunRecord { Id = "run-1", Strategy = Strategy.StrongWithGates.Id, TaskId = "t", Repo = _repo.Path, Status = RunStatus.Planned, Started = DateTimeOffset.UtcNow });
        var orchestrator = new RunOrchestrator(new ProcessRunner(), _ledger, new Supervisor(), _ => executor, gateProvider: provider);
        return await orchestrator.RunAsync(new RunRequest
        {
            RunId = "run-1",
            Contract = new Contract
            {
                Task = "Make app say v2",
                Units = [new WorkUnit { Id = "U1", Title = "Feature", Spec = "Edit app.txt", Tests = [new AcceptanceTestFile(TestPath, Original)] }],
            },
            RepoRoot = _repo.Path,
            BaseSha = _repo.Head,
            Policy = new Policy
            {
                Models = { Architect = "strong", Worker = "cheap" },
                GateExecution = new GateExecutionConfig { Provider = GateExecutionConfig.WindowsSandboxProvider },
                Acceptance = new AcceptanceConfig { Cmd = ["python", "-m", "unittest", "discover", "-s", "acceptance"] },
            },
            ExecutorName = "fake",
            Executor = new ExecutorDefinition { Cmd = ["fake"], Capabilities = { Headless = true, ReportsTokens = true } },
            Artifacts = new RunArtifacts(_config, "run-1"),
            WorkerModel = "cheap",
            UseGates = true,
            AllowEscalation = false,
            MaxAttempts = maxAttempts ?? Strategy.StrongWithGates.MaxAttempts,
        }, null, default);
    }

    [Fact]
    public async Task The_tests_are_committed_before_the_worker_starts_and_the_worker_sees_them()
    {
        var seen = new List<string>();
        var executor = new FakeExecutor((brief, dir) =>
        {
            Assert.True(File.Exists(Path.Combine(dir, TestPath)), "the worker must find the architect's test in the worktree");
            Assert.Contains(TestPath, brief.Body, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(dir, "app.txt"), "feature v2\n");
        });

        var outcome = await Run(executor, Runner(seen));

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Contains("acceptance tests", _repo.Git("log", "--format=%s", outcome.Branch), StringComparison.Ordinal);
        Assert.Equal(Original, _repo.Git("show", $"{outcome.Branch}:{TestPath}").Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal([Original], seen);
    }

    [Fact]
    public async Task A_worker_that_weakens_the_test_is_overruled_and_cannot_pass_that_way()
    {
        var seen = new List<string>();
        var executor = new FakeExecutor((_, dir) =>
        {
            File.WriteAllText(Path.Combine(dir, "app.txt"), "feature v2\n");
            File.WriteAllText(Path.Combine(dir, TestPath), "pass\n");
            File.WriteAllText(Path.Combine(dir, "acceptance", "conftest.py"), "import sys\n");
        });

        var outcome = await Run(executor, Runner(seen));

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal([Original], seen);
        Assert.Equal(Original, _repo.Git("show", $"{outcome.Branch}:{TestPath}").Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.DoesNotContain("conftest.py", _repo.Git("ls-tree", "-r", "--name-only", outcome.Branch), StringComparison.Ordinal);
        Assert.Contains("acceptance_tamper", File.ReadAllText(new RunArtifacts(_config, "run-1").TranscriptPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_unit_whose_work_does_not_satisfy_the_tests_is_not_accepted()
    {
        var seen = new List<string>();
        var executor = new FakeExecutor(FakeExecutor.Write("other.txt", "unrelated\n"));

        var outcome = await Run(executor, Runner(seen), maxAttempts: 2);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains("U1", outcome.Error, StringComparison.Ordinal);
        Assert.Contains("FAILED test_app", executor.Calls.Last().Brief.Body, StringComparison.Ordinal);
    }
}
