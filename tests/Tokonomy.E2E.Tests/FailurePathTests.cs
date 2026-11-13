#pragma warning disable CA1861 // inline arrays describe scripted architect replies
using System.Text.Json;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Supervision;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.E2E.Tests;

/// <summary>Strategy 5 when the architect times out, is down, or writes the fix itself on a risky unit.</summary>
public sealed class FailurePathTests : IDisposable
{
    private readonly TestRepo _repo = new(("app.txt", "feature v1\n"), ("README.md", "# demo\n"));
    private readonly SqliteLedger _ledger;
    private readonly string _config;

    public FailurePathTests()
    {
        _config = Path.Combine(_repo.Path, ".tokonomy");
        _ledger = new SqliteLedger(Path.Combine(_config, "ledger.db"));
        _ledger.StartRun(new RunRecord { Id = "run-1", Strategy = "5", TaskId = "t", Repo = _repo.Path, Status = RunStatus.Planned, Started = DateTimeOffset.UtcNow });
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
        Escalation = { MaxRetriesPerUnit = 1 },
        Review = { Mode = ReviewConfig.Risk },
    };

    private static Contract Plan(UnitRisk risk = UnitRisk.Low) => new()
    {
        Task = "feature v2",
        SchemaVersion = 2,
        Units = [new WorkUnit { Id = "U1", Title = "Feature", Spec = "app.txt says feature v2", Modify = ["app.txt"], Files = ["app.txt"], Steps = ["edit"], Risk = risk }],
    };

    private Task<RunOutcome> Run(Policy policy, Contract plan, FakeExecutor executor, IProvider architect) =>
        new RunOrchestrator(new ProcessRunner(), _ledger, new Supervisor(), _ => executor, architect: architect).RunAsync(new RunRequest
        {
            RunId = "run-1",
            Contract = plan,
            RepoRoot = _repo.Path,
            BaseSha = _repo.Head,
            Policy = policy,
            ExecutorName = "fake",
            Executor = new ExecutorDefinition { Cmd = ["fake"], Capabilities = { Headless = true, ReportsTokens = true } },
            Artifacts = new RunArtifacts(_config, "run-1"),
            WorkerModel = "cheap",
            AdaptiveSupervision = true,
        }, null, default);

    private sealed class Throwing(Func<Exception> make) : IProvider
    {
        public int Calls { get; private set; }

        public Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct)
        {
            Calls++;
            throw make();
        }
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("outage")]
    public async Task An_unreachable_architect_ends_the_unit_cleanly_through_the_deterministic_path(string kind)
    {
        var architect = new Throwing(() => kind == "timeout"
            ? new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 1800 seconds elapsing.")
            : new HttpRequestException("503 Service Unavailable"));
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "nothing\n"));

        var outcome = await Run(PolicyWithGate(), Plan(), executor, architect);

        Assert.Equal(RunStatus.Fail, outcome.Status);                 // a verdict, not a crash
        Assert.Equal(2, architect.Calls);                              // supervision, then the classic takeover floor
        // The failed takeover was followed by one recovery attempt, then the takeover cap ended the unit.
        Assert.Contains("takeover unavailable (takeover limit reached for this unit)", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(2, executor.Calls.Count);
        Assert.Equal(_repo.Head, outcome.HeadSha);                     // nothing unverified was committed
        Assert.Equal(["unavailable", "resumed"], _ledger.GetSummary("run-1")!.Interventions.Select(i => i.Outcome));
        Assert.Equal(RunStatus.Fail, _ledger.GetRun("run-1")!.Status);
    }

    [Fact]
    public async Task A_risky_architect_takeover_gets_an_independent_review_on_its_own_model_and_can_be_sent_back()
    {
        var policy = PolicyWithGate();
        policy.Review.ArchitectReviewModel = "second-opinion";
        var executor = new FakeExecutor(
            FakeExecutor.Write("app.txt", "nothing\n"),
            (_, dir) =>
            {
                // The worker continues from the architect's (gate-passing) fix.
                Assert.Equal("feature by architect\n", File.ReadAllText(Path.Combine(dir, "app.txt")).ReplaceLineEndings("\n"));
                File.WriteAllText(Path.Combine(dir, "app.txt"), "feature by architect, documented\n");
            });
        var architect = new FakeArchitect(
            "```json\n" + JsonSerializer.Serialize(new { level = 3, diagnosis = "rewrite", summary = "fixed", why = "deleted" }) + "\n```\n" +
            FakeArchitect.Edit("app.txt", "nothing", "feature by architect"),
            JsonSerializer.Serialize(new { verdict = "request_changes", summary = "undocumented", findings = new[] { new { severity = "medium", description = "add a note" } } }),
            JsonSerializer.Serialize(new { verdict = "approve", summary = "ok", findings = Array.Empty<object>() }));

        var outcome = await Run(policy, Plan(UnitRisk.High), executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal("second-opinion", architect.Requests[1].Model);
        Assert.Contains("You did not write this change", architect.Requests[1].User, StringComparison.Ordinal);
        Assert.Contains("add a note", executor.Calls[1].Brief.Body, StringComparison.Ordinal);
        Assert.Equal("feature by architect, documented\n", _repo.Git("show", $"{outcome.Branch}:app.txt"));
    }

    [Fact]
    public async Task An_independent_review_that_blocks_the_architects_fix_stops_the_unit_and_commits_nothing()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "nothing\n"));
        var architect = new FakeArchitect(
            "```json\n" + JsonSerializer.Serialize(new { level = 3, diagnosis = "rewrite" }) + "\n```\n" + FakeArchitect.Edit("app.txt", "nothing", "feature hack"),
            JsonSerializer.Serialize(new { verdict = "blocked", summary = "bypasses the design", findings = Array.Empty<object>() }));

        var outcome = await Run(PolicyWithGate(), Plan(UnitRisk.High), executor, architect);

        Assert.Equal(RunStatus.Fail, outcome.Status);
        Assert.Contains("bypasses the design", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(_repo.Head, outcome.HeadSha);
    }

    [Fact]
    public async Task A_small_architect_fix_on_a_routine_unit_is_not_reviewed_again()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "nothing\n"));
        var architect = new FakeArchitect(
            "```json\n" + JsonSerializer.Serialize(new { level = 2, diagnosis = "deleted the word" }) + "\n```\n" + FakeArchitect.Edit("app.txt", "nothing", "feature v2"));

        var outcome = await Run(PolicyWithGate(), Plan(UnitRisk.Low), executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Single(architect.Requests);
    }
}
