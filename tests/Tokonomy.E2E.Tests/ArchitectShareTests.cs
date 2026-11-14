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

/// <summary>
/// The architect token-share cap end to end: each worker attempt reports 1,100 tokens and each architect call 2,300
/// (FakeArchitect), so after one architect call the architect holds well over half of the run's reported tokens.
/// </summary>
public sealed class ArchitectShareTests : IDisposable
{
    private static readonly WorkerUsage[] WorkerTokens = [new WorkerUsage("cheap", 1000, 100, 0, null)];

    private readonly TestRepo _repo = new(("app.txt", "feature v1\n"), ("README.md", "# demo\n"));
    private readonly string _config;
    private readonly SqliteLedger _ledger;

    public ArchitectShareTests()
    {
        _config = Path.Combine(_repo.Path, ".tokonomy");
        _ledger = new SqliteLedger(Path.Combine(_config, "ledger.db"));
    }

    public void Dispose()
    {
        _ledger.Dispose();
        _repo.Dispose();
    }

    private static Policy CappedPolicy(string mode, double cap = 0.3) => new()
    {
        Models = { Architect = "strong", Worker = "cheap" },
        GateExecution = new GateExecutionConfig { Provider = GateExecutionConfig.HostUnsafeProvider },
        Gates = [new GateConfig { Name = "has-feature", Cmd = Shell.FileContains("app.txt", "feature") }],
        Review = { Mode = ReviewConfig.Always },
        Budget = { MaxArchitectTokenShare = cap, OnArchitectTokenShare = mode },
    };

    private static Contract TwoUnits() => new()
    {
        Task = "Add the feature and docs",
        Units =
        [
            new WorkUnit { Id = "U1", Title = "Feature", Spec = "Add 'feature' to app.txt" },
            new WorkUnit { Id = "U2", Title = "Docs", Spec = "Document it", DependsOn = ["U1"] },
        ],
    };

    private static string Approve() => JsonSerializer.Serialize(new { verdict = "approve", summary = "ok", findings = Array.Empty<object>() });

    private async Task<RunOutcome> Run(Policy policy, FakeExecutor executor, IProvider architect)
    {
        var strategy = Strategy.Adaptive;
        _ledger.StartRun(new RunRecord { Id = "run-1", Strategy = strategy.Id, TaskId = "t", Repo = _repo.Path, Status = RunStatus.Planned, Started = DateTimeOffset.UtcNow });
        var orchestrator = new RunOrchestrator(new ProcessRunner(), _ledger, new Supervisor(), _ => executor, architect: architect);
        return await orchestrator.RunAsync(new RunRequest
        {
            RunId = "run-1",
            Contract = TwoUnits(),
            RepoRoot = _repo.Path,
            BaseSha = _repo.Head,
            Policy = policy,
            ExecutorName = "fake",
            Executor = new ExecutorDefinition { Cmd = ["fake"], Capabilities = { Headless = true, ReportsTokens = true } },
            Artifacts = new RunArtifacts(_config, "run-1"),
            WorkerModel = policy.Models.Worker,
            UseGates = strategy.UseGates,
            AllowEscalation = strategy.AllowEscalation,
            AdaptiveSupervision = strategy.AdaptiveSupervision,
            MaxAttempts = strategy.MaxAttempts,
        }, null, default);
    }

    private List<JsonElement> CapEvents() =>
        _ledger.ListEvents("run-1", ArchitectShare.CapEvent).Select(p => JsonDocument.Parse(p).RootElement).ToList();

    [Fact]
    public async Task Skip_architect_skips_an_optional_review_records_why_and_the_run_continues()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"), FakeExecutor.Write("DOCS.md", "docs\n")) { Usage = WorkerTokens };
        var architect = new FakeArchitect(Approve());

        var outcome = await Run(CappedPolicy(BudgetConfig.ShareSkipArchitect), executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(["accepted", "accepted"], outcome.Units.Select(u => u.Status));
        Assert.Single(architect.Requests);
        var skipped = Assert.Single(CapEvents());
        Assert.Equal("skipped", skipped.GetProperty("action").GetString());
        Assert.Equal(CallPurposes.Review, skipped.GetProperty("purpose").GetString());
        Assert.Equal("U2", skipped.GetProperty("unit").GetString());
    }

    [Fact]
    public async Task Warn_notes_the_cap_once_and_keeps_calling_the_architect()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"), FakeExecutor.Write("DOCS.md", "docs\n")) { Usage = WorkerTokens };
        var architect = new FakeArchitect(Approve());

        var outcome = await Run(CappedPolicy(BudgetConfig.ShareWarn), executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(2, architect.Requests.Count);
        Assert.Equal("warned", Assert.Single(CapEvents()).GetProperty("action").GetString());
    }

    [Fact]
    public async Task Stop_run_stops_before_the_next_unit_and_keeps_the_accepted_one()
    {
        // Three units, so the stop is visible: U2's review is refused, then the run stops before U3's worker starts.
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"), FakeExecutor.Write("DOCS.md", "docs\n"), FakeExecutor.Write("MORE.md", "more\n"))
        {
            Usage = WorkerTokens,
        };
        var contract = TwoUnits();
        contract = contract with { Units = [.. contract.Units, new WorkUnit { Id = "U3", Title = "More", Spec = "More docs", DependsOn = ["U2"] }] };
        var architect = new FakeArchitect(Approve());
        var policy = CappedPolicy(BudgetConfig.ShareStopRun);

        var strategy = Strategy.Adaptive;
        _ledger.StartRun(new RunRecord { Id = "run-1", Strategy = strategy.Id, TaskId = "t", Repo = _repo.Path, Status = RunStatus.Planned, Started = DateTimeOffset.UtcNow });
        var outcome = await new RunOrchestrator(new ProcessRunner(), _ledger, new Supervisor(), _ => executor, architect: architect).RunAsync(new RunRequest
        {
            RunId = "run-1",
            Contract = contract,
            RepoRoot = _repo.Path,
            BaseSha = _repo.Head,
            Policy = policy,
            ExecutorName = "fake",
            Executor = new ExecutorDefinition { Cmd = ["fake"], Capabilities = { Headless = true, ReportsTokens = true } },
            Artifacts = new RunArtifacts(_config, "run-1"),
            WorkerModel = policy.Models.Worker,
            UseGates = true,
            AllowEscalation = true,
            AdaptiveSupervision = true,
            MaxAttempts = strategy.MaxAttempts,
        }, null, default);

        Assert.Equal(RunStatus.AbortedBudget, outcome.Status);
        Assert.Contains("max_architect_token_share", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(2, executor.Calls.Count);
        Assert.All(outcome.Units, u => Assert.Equal("accepted", u.Status));
    }

    [Fact]
    public async Task A_unit_that_needs_the_architect_stops_instead_of_being_bypassed_when_the_cap_is_reached()
    {
        // U1 passes and is reviewed (the architect now holds most tokens). U2 fails its gate twice: it needs supervision
        // or a takeover, both refused by the cap, so U2 must not be accepted.
        var executor = new FakeExecutor(
            FakeExecutor.Write("app.txt", "feature v2\n"),
            FakeExecutor.Write("app.txt", "nothing\n"),
            FakeExecutor.Write("app.txt", "nothing\n"),
            FakeExecutor.Write("app.txt", "nothing\n")) { Usage = WorkerTokens };
        var architect = new FakeArchitect(Approve());

        var outcome = await Run(CappedPolicy(BudgetConfig.ShareSkipArchitect), executor, architect);

        Assert.NotEqual(RunStatus.Pass, outcome.Status);
        Assert.Equal("accepted", outcome.Units[0].Status);
        Assert.NotEqual("accepted", outcome.Units[1].Status);
        Assert.Single(architect.Requests);
        Assert.Contains(CapEvents(), e => e.GetProperty("unit").GetString() == "U2" && e.GetProperty("purpose").GetString() != CallPurposes.Review);
    }

    [Fact]
    public async Task Without_worker_token_counts_the_cap_is_not_applied_and_that_is_recorded()
    {
        // The worker reports no usage at all: the share cannot be measured, so nothing is skipped (and nothing is guessed).
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"), FakeExecutor.Write("DOCS.md", "docs\n"))
        {
            Usage = [new WorkerUsage("cheap", 0, 0, 0, null)],
        };
        var architect = new FakeArchitect(Approve());

        var outcome = await Run(CappedPolicy(BudgetConfig.ShareSkipArchitect, cap: 0.01), executor, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.Equal(2, architect.Requests.Count);
        Assert.Empty(CapEvents());
        Assert.Single(_ledger.ListEvents("run-1", ArchitectShare.UnmeasurableEvent));
    }
}
