using Tokonomy.Bench;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Ledger;

namespace Tokonomy.E2E.Tests;

public sealed class BenchTokenReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tk-benchtok-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteLedger _ledger;

    public BenchTokenReportTests()
    {
        Directory.CreateDirectory(_dir);
        _ledger = new SqliteLedger(Path.Combine(_dir, "ledger.db"));
    }

    public void Dispose()
    {
        _ledger.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static BenchResultRecord Trial(string strategy, bool ok, decimal usd, params ModelUsage[] models) => new()
    {
        BenchId = "b1", TaskId = "t", Strategy = strategy, Repeat = 1, RunId = "r-" + strategy, RunStatus = RunStatus.Pass,
        Accepted = ok, Usd = usd, Duration = TimeSpan.FromMinutes(3), Models = models,
    };

    [Fact]
    public void Tokens_are_totalled_per_strategy_and_per_model_next_to_cost()
    {
        var results = new[]
        {
            Trial("4", true, 0.5m, new ModelUsage("opus", 2, 40_000, 3_000, 30_000, 0.4m), new ModelUsage("flash", 5, 120_000, 20_000, 0, 0.1m)),
            Trial("4", false, 0.3m, new ModelUsage("opus", 1, 10_000, 1_000, 0, 0.2m), new ModelUsage("flash", 3, 60_000, 10_000, 0, 0.1m)),
            Trial("2", true, 0.2m, new ModelUsage("flash", 4, 90_000, 15_000, 0, 0.2m)),
        };

        var stats = BenchReport.Summarize(results).ToDictionary(s => s.Strategy);

        Assert.Equal(50_000 + 180_000, stats["4"].InputTokens);
        Assert.Equal(4_000 + 30_000, stats["4"].OutputTokens);
        Assert.Equal(30_000, stats["4"].CachedTokens);
        Assert.Equal(264_000, stats["4"].TotalTokens);
        Assert.Equal(264_000, stats["4"].TokensPerSuccess);
        Assert.Equal(2, stats["4"].Models.Count);
        Assert.Equal("flash", stats["4"].Models[0].Model);
        Assert.Equal(105_000, stats["2"].TokensPerSuccess);
    }

    [Fact]
    public void The_markdown_report_has_token_columns_and_a_by_model_table()
    {
        var md = BenchReport.Markdown("b1", [Trial("4", true, 0.5m, new ModelUsage("opus", 2, 40_000, 3_000, 30_000, 0.4m))]);

        Assert.Contains("Tokens per success", md, StringComparison.Ordinal);
        Assert.Contains("## Tokens by model", md, StringComparison.Ordinal);
        Assert.Contains("| 4 | opus | 2 | 40k | 30k | 3.0k | $0.4000 |", md, StringComparison.Ordinal);
        Assert.Contains("3m 00s", md, StringComparison.Ordinal);
    }

    [Fact]
    public void A_strategy_with_no_pass_has_no_tokens_per_success()
    {
        Assert.Null(BenchReport.Summarize([Trial("2", false, 0.1m, new ModelUsage("flash", 1, 10, 5, 0, 0.1m))])[0].TokensPerSuccess);
    }

    [Fact]
    public void The_ledger_attaches_each_trials_token_use_by_model()
    {
        _ledger.StartRun(new RunRecord { Id = "r-4", Strategy = "4", TaskId = "t", Repo = "x", Started = DateTimeOffset.UtcNow });
        _ledger.RecordLlmCall(new LlmCallRecord { RunId = "r-4", Role = LlmRole.Architect, Model = "opus", InputTokens = 1000, OutputTokens = 100, CachedTokens = 400, Usd = 0.1m });
        _ledger.RecordLlmCall(new LlmCallRecord { RunId = "r-4", Role = LlmRole.Worker, Model = "flash", InputTokens = 7000, OutputTokens = 500, Usd = 0.01m });
        _ledger.RecordLlmCall(new LlmCallRecord { RunId = "r-4", Role = LlmRole.Worker, Model = "flash", InputTokens = 7000, OutputTokens = 500, Usd = 0.01m });
        _ledger.RecordBenchResult(Trial("4", true, 0.12m));

        var back = _ledger.ListBenchResults("b1").Single();

        Assert.Equal(15_000, back.InputTokens);
        Assert.Equal(1_100, back.OutputTokens);
        Assert.Equal(400, back.CachedTokens);
        var flash = back.Models.Single(m => m.Model == "flash");
        Assert.Equal((2, 14_000L, 1_000L), (flash.Calls, flash.InputTokens, flash.OutputTokens));
    }
}
