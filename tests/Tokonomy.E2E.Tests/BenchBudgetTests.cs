using System.Text.Json;
using Tokonomy.Bench;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Core.Supervision;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Process;

namespace Tokonomy.E2E.Tests;

/// <summary>An "agent" that makes a valid change and records a fixed spend against its run in the ledger.</summary>
internal sealed class CostingExecutor(ILedger ledger, string runId, decimal usd) : IExecutor
{
    public string Name => "costing";

    public ExecutorCapabilities Capabilities { get; } = new() { Headless = true, ReportsTokens = true };

    public Task<RunResult> RunAsync(Brief brief, string workdir, string? model, TimeSpan timeout, CancellationToken ct)
    {
        File.WriteAllText(Path.Combine(workdir, "app.txt"), "feature changed\n");
        ledger.RecordLlmCall(new LlmCallRecord { RunId = runId, Role = LlmRole.Worker, Model = "cheap", Usd = usd });
        return Task.FromResult(new RunResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1) });
    }
}

internal sealed class StopRecorder : IBenchObserver
{
    public (string Reason, int Skipped)? Stop { get; private set; }

    public void Stopped(string reason, int skippedTrials) => Stop = (reason, skippedTrials);
}

public sealed class BenchBudgetTests : IDisposable
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

    private (Taskset Taskset, TokonomyConfig Config) Setup(decimal perTaskCap = 3)
    {
        var configDir = Path.Combine(_work, ".tokonomy");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "policy.yaml"), $$"""
            models: { architect: strong, worker: cheap }
            executors: { default: fake }
            budget: { max_usd_per_task: {{perTaskCap}} }
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

    private static BenchRunner Runner(SqliteLedger ledger, decimal costPerTrial) => new(
        new ProcessRunner(), ledger, new Supervisor(), provider: null,
        executorFactory: session => new CostingExecutor(ledger, Path.GetFileName(Path.GetDirectoryName(session.LogDirectory))!, costPerTrial));

    [Fact]
    public async Task Stops_starting_trials_once_the_total_budget_is_spent()
    {
        var (taskset, config) = Setup();
        using var ledger = new SqliteLedger(config.LedgerPath);
        var observer = new StopRecorder();

        // $0.30 per trial against a $0.70 total: trials 1 and 2 leave $0.10, trial 3 still starts, then nothing remains.
        var benchId = await Runner(ledger, 0.30m).RunAsync(
            taskset, config,
            new BenchOptions { Strategies = [Strategy.CheapOnly], Repeat = 5, MaxUsd = 0.70m },
            Tokonomy.Llm.Providers.PriceTable.Empty, upstreamKey: null, observer, default);

        var results = ledger.ListBenchResults(benchId);
        Assert.Equal(3, results.Count);
        Assert.Equal(0.90m, results.Sum(r => r.Usd));
        Assert.All(results, r => Assert.True(r.Accepted));
        Assert.NotNull(observer.Stop);
        Assert.Equal(2, observer.Stop!.Value.Skipped);
        Assert.Contains("$0.70", observer.Stop.Value.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_total_cap_every_trial_runs()
    {
        var (taskset, config) = Setup();
        using var ledger = new SqliteLedger(config.LedgerPath);
        var observer = new StopRecorder();

        var benchId = await Runner(ledger, 0.30m).RunAsync(
            taskset, config,
            new BenchOptions { Strategies = [Strategy.CheapOnly], Repeat = 4 },
            Tokonomy.Llm.Providers.PriceTable.Empty, upstreamKey: null, observer, default);

        Assert.Equal(4, ledger.ListBenchResults(benchId).Count);
        Assert.Null(observer.Stop);
    }

    [Fact]
    public void Trial_cap_replaces_only_the_per_task_budget()
    {
        var policy = new Policy
        {
            Models = { Architect = "a", Worker = "w" },
            Budget = { MaxUsdPerTask = 3m, MaxUsdPerRun = 9m, MaxArchitectTokenShare = 0.4 },
            Gates = [new GateConfig { Name = "g", Cmd = ["x"] }],
        };

        var capped = BenchRunner.WithTrialCap(policy, 0.25m);

        Assert.Equal(0.25m, capped.Budget.MaxUsdPerTask);
        Assert.Equal(9m, capped.Budget.MaxUsdPerRun);
        Assert.Equal(0.4, capped.Budget.MaxArchitectTokenShare);
        Assert.Same(policy.Gates, capped.Gates);
        Assert.Equal("w", capped.Models.Worker);
        Assert.Equal(3m, policy.Budget.MaxUsdPerTask);
    }
}
