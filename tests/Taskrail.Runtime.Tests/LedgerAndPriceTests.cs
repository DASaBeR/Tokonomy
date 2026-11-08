using Taskrail.Core.Model;
using Taskrail.Llm.Providers;
using Taskrail.Runtime.Ledger;

namespace Taskrail.Runtime.Tests;

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

        Assert.Equal(2, ledger.SchemaVersion);
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
