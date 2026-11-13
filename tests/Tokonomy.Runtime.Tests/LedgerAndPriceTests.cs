using Tokonomy.Core.Model;
using Tokonomy.Llm.Providers;
using Tokonomy.Runtime.Ledger;

namespace Tokonomy.Runtime.Tests;

public class SqliteLedgerTests
{
    private static RunRecord Run(string id, DateTimeOffset started) => new()
    {
        Id = id,
        Strategy = "4",
        TaskId = "rate-limit-login",
        Repo = "demo",
        Started = started,
    };

    [Fact]
    public void Migrates_to_latest_schema()
    {
        using var tmp = new TempDir();
        using var ledger = new SqliteLedger(Path.Combine(tmp.Path, "ledger.db"));

        Assert.Equal(3, ledger.SchemaVersion);
    }

    [Fact]
    public void Summarizes_cost_per_tier()
    {
        using var tmp = new TempDir();
        using var ledger = new SqliteLedger(Path.Combine(tmp.Path, "ledger.db"));
        ledger.StartRun(Run("r1", DateTimeOffset.UtcNow));
        ledger.RecordLlmCall(new() { RunId = "r1", Role = LlmRole.Architect, Model = "strong", InputTokens = 1000, OutputTokens = 200, Usd = 0.012m });
        ledger.RecordLlmCall(new() { RunId = "r1", Role = LlmRole.Worker, Model = "cheap", InputTokens = 5000, OutputTokens = 800, Usd = 0.0021m });
        ledger.RecordLlmCall(new() { RunId = "r1", Role = LlmRole.Worker, Model = "cheap", InputTokens = 3000, OutputTokens = 400, Usd = 0.0011m });
        ledger.FinishRun("r1", RunStatus.Pass, DateTimeOffset.UtcNow);

        var summary = ledger.GetSummary("r1")!;

        Assert.Equal(RunStatus.Pass, summary.Run.Status);
        Assert.Equal(0.0152m, summary.Run.TotalUsd);
        var worker = summary.Tiers.Single(t => t.Role == LlmRole.Worker);
        Assert.Equal(2, worker.Calls);
        Assert.Equal(8000, worker.InputTokens);
        Assert.Equal(0.0032m, worker.Usd);
        Assert.False(worker.AnyEstimated);
        Assert.Equal(CostSource.Metered, summary.Run.CostSource);
    }

    [Fact]
    public void Estimated_call_marks_run_estimated()
    {
        using var tmp = new TempDir();
        using var ledger = new SqliteLedger(Path.Combine(tmp.Path, "ledger.db"));
        ledger.StartRun(Run("r1", DateTimeOffset.UtcNow));
        ledger.RecordLlmCall(new() { RunId = "r1", Role = LlmRole.Worker, Model = "cheap", Source = CostSource.Estimated });

        Assert.Equal(CostSource.Estimated, ledger.GetRun("r1")!.CostSource);
        Assert.True(ledger.GetSummary("r1")!.Tiers.Single().AnyEstimated);
    }

    [Fact]
    public void Lists_runs_newest_first_and_survives_reopen()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "ledger.db");
        var t0 = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        using (var ledger = new SqliteLedger(path))
        {
            ledger.StartRun(Run("old", t0));
            ledger.StartRun(Run("new", t0.AddHours(1)));
        }

        using var reopened = new SqliteLedger(path);
        var runs = reopened.ListRuns(10);

        Assert.Equal(["new", "old"], runs.Select(r => r.Id));
        Assert.Equal(t0, runs[1].Started);
        Assert.Null(reopened.GetSummary("missing"));
    }

    [Theory]
    [InlineData("0.0000005", 1)]
    [InlineData("0.0000004", 0)]
    [InlineData("1.25", 1_250_000)]
    public void Money_is_stored_as_micros(string usd, long micros) =>
        Assert.Equal(micros, SqliteLedger.ToMicros(decimal.Parse(usd, System.Globalization.CultureInfo.InvariantCulture)));
}

public class PriceTableTests
{
    private static readonly PriceTable Table = new(new Dictionary<string, ModelPrice>
    {
        ["vendor/strong"] = new() { Input = 3m, Output = 15m, CachedInput = 0.3m },
        ["vendor/cheap"] = new() { Input = 0.2m, Output = 0.8m },
    });

    [Fact]
    public void Costs_uncached_cached_and_output_tokens()
    {
        // 600k uncached * $3 + 400k cached * $0.30 + 100k out * $15 = 1.8 + 0.12 + 1.5
        Assert.Equal(3.42m, Table.Cost("vendor/strong", 1_000_000, 100_000, 400_000));
    }

    [Fact]
    public void Cached_price_defaults_to_input_price() =>
        Assert.Equal(0.2m + 0.8m, Table.Cost("vendor/cheap", 1_000_000, 1_000_000, 500_000));

    // claude-haiku-5-5 on AvalAI, as published (USD per million tokens).
    private const string HaikuYaml = """
        models:
          claude-haiku-5-5:
            input: 0.1
            cached_input: 0.01
            cache_write: 0.2
            output: 0.5
            above_input_tokens: 100000
            above:
              input: 0.5
              cached_input: 0.05
              cache_write: 1
              output: 2.5
        """;

    [Fact]
    public void Long_context_requests_use_the_above_rates_for_the_whole_request()
    {
        using var tmp = new TempDir();
        var table = PriceTable.Load(tmp.File("prices.yaml", HaikuYaml));

        // At the threshold: base rates. 100k * 0.1 + 1k * 0.5 = 10.5 per million → 0.0105
        Assert.Equal(0.0105m, table.Cost("claude-haiku-5-5", 100_000, 1_000, 0));
        // One token over: every token is repriced. 100,001 * 0.5 + 1k * 2.5, per million.
        Assert.Equal((100_001m * 0.5m + 1_000m * 2.5m) / 1_000_000m, table.Cost("claude-haiku-5-5", 100_001, 1_000, 0));
    }

    [Fact]
    public void Cache_writes_have_their_own_rate_and_default_to_input()
    {
        using var tmp = new TempDir();
        var table = PriceTable.Load(tmp.File("prices.yaml", HaikuYaml));

        // 50k uncached * 0.1 + 30k cached * 0.01 + 20k write * 0.2 + 1k out * 0.5 = 5000 + 300 + 4000 + 500 per million
        Assert.Equal(0.0098m, table.Cost("claude-haiku-5-5", 100_000, 1_000, 30_000, 20_000));
        // A model with no cache_write rate bills writes like normal input.
        Assert.Equal(0.2m + 0.8m, Table.Cost("vendor/cheap", 1_000_000, 1_000_000, 0, cacheWriteTokens: 500_000));
    }

    [Theory]
    [InlineData("above_input_tokens: 1000")]
    [InlineData("above: { input: 1, output: 1 }")]
    [InlineData("above_input_tokens: 0\n    above: { input: 1, output: 1 }")]
    [InlineData("cache_write: -1")]
    public void Invalid_tier_or_negative_prices_are_rejected(string extra)
    {
        using var tmp = new TempDir();
        var path = tmp.File("prices.yaml", $"models:\n  m:\n    input: 1\n    output: 1\n    {extra}\n");

        Assert.Throws<InvalidDataException>(() => PriceTable.Load(path));
    }

    [Fact]
    public void Unknown_model_has_no_cost() => Assert.Null(Table.Cost("vendor/unknown", 10, 10, 0));

    [Fact]
    public void Lookup_is_case_insensitive() => Assert.True(Table.Has("VENDOR/Strong"));

    [Fact]
    public void Loads_yaml_file()
    {
        using var tmp = new TempDir();
        var path = tmp.File("prices.yaml", "models:\n  \"a/b\":\n    input: 1.5\n    output: 2\n    cached_input: 0.5\n");

        var table = PriceTable.Load(path);

        Assert.Equal(1.5m + 2m, table.Cost("a/b", 1_000_000, 1_000_000, 0));
    }

    [Fact]
    public void Missing_file_is_empty_table() => Assert.Empty(PriceTable.Load("does-not-exist.yaml").Models);
}
