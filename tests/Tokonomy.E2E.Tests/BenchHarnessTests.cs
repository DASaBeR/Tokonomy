using System.Text.Json;
using Tokonomy.Bench;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Supervision;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.E2E.Tests;

/// <summary>Milestone 3 harness: the direct arm, seeded trial order, run caps, several independent judges, the manifest and the report.</summary>
public sealed class BenchHarnessTests : IDisposable
{
    private readonly TestRepo _repo = new(("app.txt", "feature v1\n"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "tokonomy-e2e", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _repo.Dispose();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_work, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private (Taskset Taskset, TokonomyConfig Config) Setup()
    {
        var configDir = Path.Combine(_work, ".tokonomy");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "policy.yaml"), """
            models: { architect: strong, worker: cheap }
            executors: { default: fake }
            gate_execution: { provider: host-unsafe }
            budget: { max_usd_per_task: 3 }
            """);
        File.WriteAllText(Path.Combine(configDir, "executors.yaml"), """
            executors:
              fake:
                cmd: ["fake"]
                capabilities: { headless: true, model_override: true, reports_tokens: true }
            """);
        File.WriteAllText(Path.Combine(_work, "tasks.yaml"), $"""
            config: .tokonomy
            tasks:
              - id: t1
                repo: {JsonSerializer.Serialize(_repo.Path)}
                prompt: change app.txt
                acceptance:
                  cmd: {Shell.YamlArray(Shell.FileContains("app.txt", "feature"))}
            """);
        return (Taskset.Load(Path.Combine(_work, "tasks.yaml")), ConfigLoader.Load(configDir));
    }

    private static BenchRunner Runner(SqliteLedger ledger, params BenchJudge[] judges) => new(
        new ProcessRunner(), ledger, new Supervisor(), provider: null,
        executorFactory: session => new CostingExecutor(ledger, Path.GetFileName(Path.GetDirectoryName(session.LogDirectory))!, 0.01m),
        judges: judges);

    private sealed class ScriptedJudge(string text, decimal? usd = null, bool fail = false) : IProvider
    {
        public int Calls { get; private set; }

        public Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct)
        {
            Calls++;
            return fail
                ? throw new InvalidOperationException("judge is down")
                : Task.FromResult(new ProviderResponse(text, request.Model, 100, 20, 0, usd));
        }
    }

    private const string Approve = "VERDICT: APPROVE\nSUMMARY: fine";
    private const string Concerns = "VERDICT: CONCERNS\nSUMMARY: not fine\nCONCERNS:\n- app.txt is wrong";

    private static JsonDocument Manifest(TokonomyConfig config, string benchId) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(config.ConfigDirectory, "runs", benchId + ".manifest.json")));

    [Fact]
    public void Shuffle_is_deterministic_for_a_seed_and_differs_between_seeds()
    {
        var a = Enumerable.Range(1, 30).ToList();
        var b = Enumerable.Range(1, 30).ToList();
        var c = Enumerable.Range(1, 30).ToList();
        BenchRunner.Shuffle(a, 7);
        BenchRunner.Shuffle(b, 7);
        BenchRunner.Shuffle(c, 8);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(Enumerable.Range(1, 30), a);
        Assert.Equal(Enumerable.Range(1, 30), a.Order());
    }

    [Fact]
    public async Task Seed_and_the_resulting_trial_order_are_recorded_in_the_manifest_together_with_preregistration_and_judges()
    {
        var (taskset, config) = Setup();
        using var ledger = new SqliteLedger(config.LedgerPath);

        var benchId = await Runner(ledger, new BenchJudge("j1", new ScriptedJudge(Approve), "judge-model")).RunAsync(
            taskset, config,
            new BenchOptions
            {
                Strategies = [Strategy.CheapOnly, Strategy.Direct],
                Repeat = 3,
                Seed = 42,
                Judge = true,
                Preregistration = new Preregistration { Hypothesis = "H", SuccessCriteria = "S", ExclusionRules = "E" },
            },
            Tokonomy.Llm.Providers.PriceTable.Empty, upstreamKey: null, observer: null, default);

        using var manifest = Manifest(config, benchId);
        var root = manifest.RootElement;
        Assert.Equal(42, root.GetProperty("seed").GetInt32());
        var order = root.GetProperty("trial_order").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(6, order.Count);
        Assert.Equal(6, order.Distinct().Count());
        var ran = ledger.ListBenchResults(benchId).Select(r => $"{r.TaskId}/{r.Strategy}/{r.Repeat}").ToList();
        Assert.Equal(order, ran); // the recorded order is the order that actually ran
        Assert.Equal("j1", root.GetProperty("judges")[0].GetProperty("name").GetString());
        Assert.Equal("E", root.GetProperty("preregistration").GetProperty("exclusion_rules").GetString());
        Assert.Equal(12, root.GetProperty("preregistration").GetProperty("sha").GetString()!.Length);
        Assert.Equal(12, root.GetProperty("judge_rubric_sha").GetString()!.Length);
    }

    [Fact]
    public async Task Max_trials_is_a_hard_cap_on_the_number_of_trials_started()
    {
        var (taskset, config) = Setup();
        using var ledger = new SqliteLedger(config.LedgerPath);

        var benchId = await Runner(ledger).RunAsync(
            taskset, config, new BenchOptions { Strategies = [Strategy.CheapOnly, Strategy.Direct], Repeat = 5, MaxTrials = 3 },
            Tokonomy.Llm.Providers.PriceTable.Empty, upstreamKey: null, observer: null, default);

        Assert.Equal(3, ledger.ListBenchResults(benchId).Count);
    }

    private sealed class LimitedExecutor : IExecutor
    {
        public string Name => "limited";

        public ExecutorCapabilities Capabilities { get; } = new() { Headless = true };

        public Task<RunResult> RunAsync(Brief brief, string workdir, string? model, TimeSpan timeout, CancellationToken ct) =>
            throw new InvalidOperationException("usage limit reached, try again later");
    }

    [Fact]
    public async Task Consecutive_environment_errors_stop_the_benchmark_instead_of_counting_as_failed_strategies()
    {
        var (taskset, config) = Setup();
        using var ledger = new SqliteLedger(config.LedgerPath);
        var observer = new StopRecorder();
        var runner = new BenchRunner(new ProcessRunner(), ledger, new Supervisor(), provider: null, executorFactory: _ => new LimitedExecutor());

        var benchId = await runner.RunAsync(
            taskset, config, new BenchOptions { Strategies = [Strategy.CheapOnly], Repeat = 6, StopAfterConsecutiveInfraFailures = 2 },
            Tokonomy.Llm.Providers.PriceTable.Empty, upstreamKey: null, observer, default);

        var results = ledger.ListBenchResults(benchId);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(BenchReport.IsInfrastructureFailure(r), r.Note));
        Assert.NotNull(observer.Stop);
        Assert.Equal(4, observer.Stop!.Value.Skipped);
        Assert.Contains("environment error", observer.Stop.Value.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_direct_arm_runs_end_to_end_and_is_accepted_by_the_hidden_check()
    {
        var (taskset, config) = Setup();
        using var ledger = new SqliteLedger(config.LedgerPath);

        var benchId = await Runner(ledger).RunAsync(
            taskset, config, new BenchOptions { Strategies = [Strategy.Direct], Repeat = 1 },
            Tokonomy.Llm.Providers.PriceTable.Empty, upstreamKey: null, observer: null, default);

        var result = Assert.Single(ledger.ListBenchResults(benchId));
        Assert.Equal("direct", result.Strategy);
        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task Several_judges_are_recorded_separately_and_a_failing_judge_never_loses_the_trial()
    {
        var (taskset, config) = Setup();
        using var ledger = new SqliteLedger(config.LedgerPath);

        var benchId = await Runner(ledger,
                new BenchJudge("opus", new ScriptedJudge(Approve), "opus"),
                new BenchJudge("gemini", new ScriptedJudge(Concerns), "gemini"),
                new BenchJudge("deepseek", new ScriptedJudge("", fail: true), "deepseek"))
            .RunAsync(taskset, config, new BenchOptions { Strategies = [Strategy.CheapOnly], Repeat = 1, Judge = true },
                Tokonomy.Llm.Providers.PriceTable.Empty, upstreamKey: null, observer: null, default);

        var result = Assert.Single(ledger.ListBenchResults(benchId));
        Assert.True(result.Accepted);
        Assert.Equal("Approve", result.Stats.JudgeVerdicts["opus"]);
        Assert.Equal("Concerns", result.Stats.JudgeVerdicts["gemini"]);
        Assert.Equal("Unavailable", result.Stats.JudgeVerdicts["deepseek"]);

        var markdown = BenchReport.Markdown(benchId, ledger.ListBenchResults(benchId));
        Assert.Contains("## Judges (reported separately)", markdown, StringComparison.Ordinal);
        Assert.Contains("| Strategy | deepseek | gemini | opus | Unanimous approve |", markdown, StringComparison.Ordinal);
        Assert.Contains("0/1", markdown, StringComparison.Ordinal); // not unanimous: gemini had concerns
    }

    [Fact]
    public async Task Metered_judges_stop_when_the_judge_budget_is_spent_and_unmetered_ones_keep_going()
    {
        var (taskset, config) = Setup();
        using var ledger = new SqliteLedger(config.LedgerPath);
        var paid = new ScriptedJudge(Approve, usd: 0.05m);
        var free = new ScriptedJudge(Approve, usd: 0.50m);

        var benchId = await Runner(ledger, new BenchJudge("paid", paid, "p", Metered: true), new BenchJudge("sub", free, "s"))
            .RunAsync(taskset, config, new BenchOptions { Strategies = [Strategy.CheapOnly], Repeat = 3, Judge = true, MaxJudgeUsd = 0.05m },
                Tokonomy.Llm.Providers.PriceTable.Empty, upstreamKey: null, observer: null, default);

        var results = ledger.ListBenchResults(benchId);
        Assert.Equal(3, results.Count);
        Assert.Equal(1, paid.Calls);   // $0.05 spent on the first trial, then the cap holds
        Assert.Equal(3, free.Calls);   // the subscription judge is not subject to the money cap
        Assert.Equal(2, results.Count(r => r.Stats.JudgeVerdicts["paid"] == "Unavailable"));
    }

    [Fact]
    public void The_report_shows_bootstrap_intervals_and_marks_them_directional()
    {
        var results = Enumerable.Range(1, 10).Select(i => new BenchResultRecord
        {
            BenchId = "b", TaskId = "t", Strategy = "direct", Repeat = i, RunId = "r" + i, RunStatus = RunStatus.Pass,
            Accepted = i <= 7, Usd = 0.10m, CostSource = CostSource.Estimated, Duration = TimeSpan.FromSeconds(5),
        }).ToList();

        var markdown = BenchReport.Markdown("b", results);

        Assert.Contains("## Uncertainty (directional only)", markdown, StringComparison.Ordinal);
        Assert.Contains("70% (", markdown, StringComparison.Ordinal);
        Assert.Contains("use plan quota, not cash", markdown, StringComparison.Ordinal);
        var (lo, hi) = BenchReport.Bootstrap(results, ts => (double)ts.Count(t => t.Accepted) / ts.Count);
        Assert.InRange(lo, 0.3, 0.7);
        Assert.InRange(hi, 0.7, 1.0);
        Assert.Equal(BenchReport.Bootstrap(results, ts => (double)ts.Count(t => t.Accepted) / ts.Count), (lo, hi)); // fixed seed
    }

    [Fact]
    public void Taskset_validation_rejects_bad_judges_and_accepts_good_ones()
    {
        string Write(string judges)
        {
            var path = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".yaml");
            Directory.CreateDirectory(_work);
            var lines = new[] { "judges:", judges, "tasks:", "  - id: t1", "    repo: .", "    prompt: p", "    acceptance: { cmd: [\"true\"] }" };
            File.WriteAllText(path, string.Join(Environment.NewLine, lines));
            return path;
        }

        var ok = Taskset.Load(Write("""
              - { name: deepseek, provider: api, model: deepseek-v4.1-flash, base_url: "https://api.example.test/v1", api_key_env: JUDGE_KEY }
              - { name: opus, provider: claude-cli, model: opus }
            """));
        Assert.Equal(2, ok.Judges.Count);

        var noKeyName = Assert.Throws<InvalidDataException>(() => Taskset.Load(Write("""
              - { name: d, provider: api, model: m, base_url: "https://x/v1" }
            """)));
        Assert.Contains("api_key_env", noKeyName.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => Taskset.Load(Write("""
              - { name: d, provider: telepathy, model: m }
            """)));
        Assert.Throws<InvalidDataException>(() => Taskset.Load(Write("""
              - { name: d, provider: claude-cli, model: m }
              - { name: d, provider: agy-cli, model: m }
            """)));
    }
}
