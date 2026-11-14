#pragma warning disable CA1861 // inline arrays describe scripted architect replies
using System.Reflection;
using System.Text.Json;
using Tokonomy.Cli.Commands;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Supervision;
using Tokonomy.Llm.Providers;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.E2E.Tests;

/// <summary>
/// Milestone 0: one test per known defect (D1-D10 in the private audit table). EVERY TEST HERE IS EXPECTED TO FAIL on the current code
/// (they are "red" on purpose) and must pass once the matching fix lands. Run only these with
/// <c>dotnet test --filter Category=DefectRepro</c>; run the rest of the suite without them with
/// <c>dotnet test --filter "Category!=DefectRepro"</c>.
/// </summary>
[Trait("Category", "DefectRepro")]
public sealed class DefectReproductionTests : IDisposable
{
    private readonly TestRepo _repo = new(("app.txt", "feature v1\n"), ("README.md", "# demo\n"));
    private readonly string _config;
    private readonly SqliteLedger _ledger;

    public DefectReproductionTests()
    {
        _config = Path.Combine(_repo.Path, ".tokonomy");
        _ledger = new SqliteLedger(Path.Combine(_config, "ledger.db"));
    }

    public void Dispose()
    {
        _ledger.Dispose();
        _repo.Dispose();
    }

    private static Policy PolicyWithGate(string review = ReviewConfig.Off) => new()
    {
        Models = { Architect = "strong", Worker = "cheap" },
        GateExecution = new GateExecutionConfig { Provider = GateExecutionConfig.HostUnsafeProvider },
        Gates = [new GateConfig { Name = "has-feature", Cmd = Shell.FileContains("app.txt", "feature") }],
        Review = { Mode = review },
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

    private async Task<RunOutcome> Run(Contract contract, Policy policy, FakeExecutor executor, Strategy strategy, IProvider? architect, PriceTable? prices = null)
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
            WorkerModel = policy.Models.Worker,
            UseGates = strategy.UseGates,
            AllowEscalation = strategy.AllowEscalation,
            AdaptiveSupervision = strategy.AdaptiveSupervision,
            MaxAttempts = strategy.MaxAttempts,
            Prices = prices ?? PriceTable.Empty,
        }, null, default);
    }

    private static string Approve() => JsonSerializer.Serialize(new { verdict = "approve", summary = "follows the plan", findings = Array.Empty<object>() });

    // ------------------------------------------------------------------------------------------------ D1

    /// <summary>D1: agy-cli and codex-cli architect providers return tokens but no dollar figure (Usd = null).</summary>
    private sealed class TokensOnlyArchitect(string reply) : IProvider
    {
        public Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct) =>
            Task.FromResult(new ProviderResponse(reply, request.Model, 1_000_000, 100_000, 0, null, Estimated: true));
    }

    [Fact]
    [Trait("Defect", "D1")]
    public async Task D1_architect_tokens_without_a_reported_cost_are_priced_from_the_table_instead_of_counting_as_free()
    {
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"));
        var prices = new PriceTable(new Dictionary<string, ModelPrice> { ["strong"] = new() { Input = 1m, Output = 10m } });
        var contract = new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "Feature", Spec = "Add 'feature' to app.txt" }] };

        // Adaptive supervision with review on: the architect reviews U1 and reports 1M input + 100k output tokens, no USD.
        var outcome = await Run(contract, PolicyWithGate(ReviewConfig.Always), executor, Strategy.Adaptive, new TokensOnlyArchitect(Approve()), prices);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        var architect = _ledger.GetSummary("run-1")!.Tiers.Single(t => t.Role == LlmRole.Architect);
        Assert.Equal(1, architect.Calls);

        // 1M x $1 + 100k x $10 per million = $2.00 API-equivalent. Today the call is recorded as $0 and never touches the budget.
        Assert.Equal(2.0m, architect.Usd);
        Assert.Equal(2.0m, outcome.TotalUsd);
    }

    // ------------------------------------------------------------------------------------------------ D2

    [Fact]
    [Trait("Defect", "D2")]
    public async Task D2_budget_max_usd_per_run_stops_a_run_that_has_spent_more_than_it()
    {
        // Each architect review costs $0.02 (FakeArchitect). The per-run cap is $0.01; the per-task budget stays at the default $3.
        var policy = PolicyWithGate(ReviewConfig.Always);
        policy.Budget.MaxUsdPerRun = 0.01m;
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"), FakeExecutor.Write("DOCS.md", "docs\n"));

        // "Run" here means one orchestrator run (one `tokonomy run`), not a bench session. If the chosen fix is to REMOVE
        // the key instead, delete this test and add a config-validation test that rejects it.
        // U1's review pushes spend past the cap, so U2's worker must never start.
        var outcome = await Run(TwoUnits(), policy, executor, Strategy.Adaptive, new FakeArchitect(Approve()));

        Assert.Equal(RunStatus.AbortedBudget, outcome.Status);
        Assert.Single(outcome.Units, u => u.Status == "accepted");
        Assert.Single(executor.Calls);
    }

    [Fact]
    [Trait("Defect", "D2")]
    public async Task D2_budget_max_architect_token_share_stops_further_architect_calls_once_it_is_exceeded()
    {
        // Decided meaning (default on_architect_token_share: skip_architect): judged on reported tokens (planning not
        // counted); once the architect's share is above the cap, no further architect call is made, and the run continues.
        // Each worker attempt reports 1,100 tokens; each architect review 2,300, so after U1's review the share is ~68%.
        var policy = PolicyWithGate(ReviewConfig.Always);
        policy.Budget.MaxArchitectTokenShare = 0.5;
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"), FakeExecutor.Write("DOCS.md", "docs\n"))
        {
            Usage = [new WorkerUsage("cheap", 1000, 100, 0, null)],
        };
        var architect = new FakeArchitect(Approve());

        var outcome = await Run(TwoUnits(), policy, executor, Strategy.Adaptive, architect);

        // U1's review is the only architect call; U2's (optional) review is skipped and U2 is still accepted.
        Assert.Single(architect.Requests);
        Assert.Equal(RunStatus.Pass, outcome.Status);
    }

    // ------------------------------------------------------------------------------------------------ D3

    [Fact(Skip = "Open defect D3: fixed by resumable human decisions (Milestone 4); remove this Skip with the fix")]
    [Trait("Defect", "D3")]
    public async Task D3_a_run_stopped_for_a_human_decision_is_paused_not_failed()
    {
        // Touching a protected path needs a human decision (the architect may not edit it). Today the unit fails, the run
        // ends as Fail, and `run --resume` refuses Fail runs, so there is no way to continue after a human approves.
        // Target design (roadmap 0.2): the run pauses in an awaiting-decision state, `tokonomy decide` records the answer,
        // and only then does resume continue. This test checks only the part that exists before `decide` is built: the run
        // must not end as a verdict (Fail) or as an error (Error, which resume would accept without any decision).
        var policy = PolicyWithGate();
        policy.Escalation.ProtectedPaths = ["config/secrets.json"];
        var executor = new FakeExecutor((_, dir) =>
        {
            File.WriteAllText(Path.Combine(dir, "app.txt"), "feature v2\n");
            Directory.CreateDirectory(Path.Combine(dir, "config"));
            File.WriteAllText(Path.Combine(dir, "config", "secrets.json"), "{}\n");
        });
        var contract = new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "Feature", Spec = "Add 'feature' to app.txt" }] };
        new RunArtifacts(_config, "run-1").SavePlan(new PlanFile
        {
            RunId = "run-1",
            Task = "t",
            Strategy = Strategy.Supervised.Id,
            BaseSha = _repo.Head,
            Created = DateTimeOffset.UtcNow,
            Contract = contract,
        });

        var outcome = await Run(contract, policy, executor, Strategy.Supervised, new FakeArchitect("unused"));

        // Precondition: this really is the human-decision stop, not some other failure.
        Assert.Contains("needs a human decision", outcome.Units.Single().Reason, StringComparison.Ordinal);

        var status = _ledger.GetRun("run-1")!.Status;
        Assert.True(status is not RunStatus.Fail and not RunStatus.Error, $"a run waiting for a human decision ended as {status}");

        // Today PrepareResume refuses it as a verdict. After the fix it should refuse with "waiting for a decision" until
        // `tokonomy decide` records one; add the approve -> resume -> Pass end-to-end test when `decide` exists.
        var config = new TokonomyConfig { ConfigDirectory = _config, Policy = policy, Executors = new ExecutorsFile(), Issues = [] };
        var (problem, _) = RunCommand.PrepareResume(config, _ledger, "run-1");
        Assert.DoesNotContain("failed on a verdict", problem ?? "", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ D4

    [Fact(Skip = "Open defect D4: fixed by full-coverage review (Milestone 5); remove this Skip with the fix")]
    [Trait("Defect", "D4")]
    public async Task D4_the_unit_review_sees_every_changed_file_of_a_large_diff()
    {
        // Ten files of about 21,000 characters each, far above the 40,000-character review cap. A correct fix may review in
        // several calls (roadmap 0.3), so the check is across ALL review requests: every file's last line must be seen.
        var paths = Enumerable.Range(1, 10).Select(i => $"src/Module{i}/File{i}.cs").ToList();
        var executor = new FakeExecutor((_, dir) =>
        {
            File.WriteAllText(Path.Combine(dir, "app.txt"), "feature v2\n");
            foreach (var path in paths)
            {
                var full = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllLines(full, Enumerable.Range(0, 300).Select(n => $"// line {n} of {path} with some padding text to make it longer"));
            }
        });
        var architect = new FakeArchitect(Approve());
        var contract = new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "Modules", Spec = "Add ten modules and update app.txt" }] };

        var outcome = await Run(contract, PolicyWithGate(ReviewConfig.Always), executor, Strategy.Adaptive, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        Assert.NotEmpty(architect.Requests);
        var seen = string.Join("\n", architect.Requests.Select(r => r.User));
        var missing = paths.Where(p => !seen.Contains($"line 299 of {p}", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, $"the review never saw the end of {missing.Count} of {paths.Count} changed files: {string.Join(", ", missing)}");
    }

    // ------------------------------------------------------------------------------------------------ D8

    [Fact]
    [Trait("Defect", "D8")]
    public void D8_run_again_labels_the_new_run_with_the_strategy_the_plan_will_actually_run_with()
    {
        // `run --again` copies the ledger row's strategy, but `run` executes the strategy stored in plan.json.
        // A reused plan (e.g. made by strategy 4, then run as strategy 5) therefore gets a wrong label in `report`.
        var config = new TokonomyConfig
        {
            ConfigDirectory = _config,
            Policy = new Policy(),
            Executors = new ExecutorsFile(),
            Issues = [],
        };
        var source = new RunRecord { Id = "src-run", Strategy = "4", TaskId = "t", Repo = _repo.Path, Status = RunStatus.Pass, Started = DateTimeOffset.UtcNow };
        _ledger.StartRun(source);
        new RunArtifacts(_config, source.Id).SavePlan(new PlanFile
        {
            RunId = source.Id,
            Task = "t",
            Strategy = "5",
            BaseSha = _repo.Head,
            Created = DateTimeOffset.UtcNow,
            Contract = new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "Feature", Spec = "x" }] },
        });

        var copy = typeof(RunCommand).GetMethod("CopyPlanToNewRun", BindingFlags.NonPublic | BindingFlags.Static)
                   ?? throw new InvalidOperationException("RunCommand.CopyPlanToNewRun not found (renamed? update this reproduction).");
        var newId = (string)copy.Invoke(null, [config, _ledger, source])!;

        Assert.Equal("5", _ledger.GetRun(newId)!.Strategy);
    }

    // ------------------------------------------------------------------------------------------------ D10

    /// <summary>Delays every architect reply, like a real model call, so a measured duration cannot round to zero.</summary>
    private sealed class SlowArchitect(IProvider inner, TimeSpan delay) : IProvider
    {
        public async Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return await inner.CompleteAsync(request, ct);
        }
    }

    [Fact]
    [Trait("Defect", "D10")]
    public async Task D10_an_architect_takeover_attempt_records_how_long_the_architect_call_took()
    {
        var executor = new FakeExecutor(
            FakeExecutor.Write("app.txt", "nothing\n"),
            FakeExecutor.Write("app.txt", "nothing\n"),
            FakeExecutor.Write("DOCS.md", "docs\n"));
        var architect = new SlowArchitect(new FakeArchitect(FakeArchitect.Edit("app.txt", "nothing", "feature fixed")), TimeSpan.FromMilliseconds(300));

        var outcome = await Run(TwoUnits(), PolicyWithGate(), executor, Strategy.Supervised, architect);

        Assert.Equal(RunStatus.Pass, outcome.Status);
        var takeover = _ledger.ListAttempts("run-1").Single(a => a.Executor == "architect");
        Assert.True(takeover.Duration >= TimeSpan.FromMilliseconds(250), $"architect attempt duration was {takeover.Duration}");
    }

    [Fact]
    [Trait("Defect", "D10")]
    public async Task D10_every_gate_run_records_how_long_the_gate_took()
    {
        // A gate that takes at least 400 ms. The ledger must store its duration per gate run (column name per the plan:
        // gate_results.duration_ms). Today the column does not exist, so the query below fails.
        var policy = PolicyWithGate();
        policy.Gates = [new GateConfig
        {
            Name = "slow-has-feature",
            Cmd = Shell.Cmd(
                "sleep 0.4; grep -q feature app.txt",
                "Start-Sleep -Milliseconds 400; if (-not (Select-String -Quiet -Pattern feature -Path app.txt)) { exit 1 }"),
        }];
        var executor = new FakeExecutor(FakeExecutor.Write("app.txt", "feature v2\n"));
        var contract = new Contract { Task = "t", Units = [new WorkUnit { Id = "U1", Title = "Feature", Spec = "Add 'feature' to app.txt" }] };

        var outcome = await Run(contract, policy, executor, Strategy.Supervised, architect: null);
        Assert.Equal(RunStatus.Pass, outcome.Status);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_config, "ledger.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT duration_ms FROM gate_results";
        var durations = new List<long>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                durations.Add(reader.GetInt64(0));
            }
        }

        Assert.NotEmpty(durations);
        Assert.All(durations, ms => Assert.True(ms >= 350, $"gate duration recorded as {ms} ms"));
    }
}
