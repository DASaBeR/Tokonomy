using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Llm.Providers;
using Tokonomy.Runtime.Ledger;
using Tokonomy.Runtime.Orchestration;
using Tokonomy.Runtime.Providers;

namespace Tokonomy.Runtime.Tests;

/// <summary>Honest cost accounting: tokens without a reported cost are priced, unpriced usage is Unknown (never "free").</summary>
public class CostAccountingTests
{
    private static readonly PriceTable Prices = new(new Dictionary<string, ModelPrice> { ["strong"] = new() { Input = 1m, Output = 10m } });

    private sealed class Fixed(ProviderResponse response) : IProvider
    {
        public Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct) => Task.FromResult(response);
    }

    [Fact]
    public async Task A_tokens_only_answer_is_priced_from_the_table_and_marked_estimated()
    {
        var provider = new PricedProvider(new Fixed(new ProviderResponse("ok", "strong", 1_000_000, 100_000, 0, null, Estimated: true)), Prices);

        var response = await provider.CompleteAsync(new ProviderRequest("strong", "s", "u"), default);

        Assert.Equal(2.0m, response.Usd);
        Assert.True(response.Estimated);
    }

    [Fact]
    public async Task The_configured_model_name_is_used_when_the_answer_names_the_model_differently()
    {
        var provider = new PricedProvider(new Fixed(new ProviderResponse("ok", "strong-2026-11-01", 1_000_000, 0, 0, null, Estimated: true)), Prices);

        var response = await provider.CompleteAsync(new ProviderRequest("strong", "s", "u"), default);

        Assert.Equal(1.0m, response.Usd);
    }

    [Fact]
    public async Task An_answer_that_already_has_a_cost_passes_through_unchanged()
    {
        var original = new ProviderResponse("ok", "strong", 1_000_000, 100_000, 0, 0.5m);
        var response = await new PricedProvider(new Fixed(original), Prices).CompleteAsync(new ProviderRequest("strong", "s", "u"), default);

        Assert.Same(original, response);
    }

    [Fact]
    public void An_architect_call_without_a_cost_or_a_price_is_unknown_not_free()
    {
        var (usd, source) = UsagePricing.ForArchitect(new ProviderResponse("ok", "unpriced", 5000, 500, 0, null, Estimated: true), Prices);

        Assert.Equal(0m, usd);
        Assert.Equal(CostSource.Unknown, source);
    }

    [Fact]
    public void A_metered_architect_cost_keeps_its_source()
    {
        Assert.Equal((0.3m, CostSource.Metered), UsagePricing.ForArchitect(new ProviderResponse("ok", "strong", 1, 1, 0, 0.3m), Prices));
        Assert.Equal((0.3m, CostSource.Estimated), UsagePricing.ForArchitect(new ProviderResponse("ok", "strong", 1, 1, 0, 0.3m, Estimated: true), Prices));
    }

    [Fact]
    public void Unknown_usage_marks_the_run_unknown_and_a_later_estimate_does_not_hide_it()
    {
        using var tmp = new TempDir();
        using var ledger = new SqliteLedger(Path.Combine(tmp.Path, "ledger.db"));
        ledger.StartRun(new RunRecord { Id = "r1", Strategy = "4", TaskId = "t", Repo = "demo", Started = DateTimeOffset.UtcNow });

        ledger.RecordLlmCall(new LlmCallRecord { RunId = "r1", Role = LlmRole.Architect, Model = "unpriced", InputTokens = 10, Source = CostSource.Unknown });
        ledger.RecordLlmCall(new LlmCallRecord { RunId = "r1", Role = LlmRole.Worker, Model = "cheap", Usd = 0.1m, Source = CostSource.Estimated });
        ledger.MarkEstimated("r1");

        Assert.Equal(CostSource.Unknown, ledger.GetRun("r1")!.CostSource);
        Assert.True(ledger.GetSummary("r1")!.Tiers.Single(t => t.Role == LlmRole.Architect).AnyEstimated);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, ArchitectShare.Verdict.Allow)]               // no worker usage yet: nothing to compare with
    [InlineData(5000, 0, 1, 1, ArchitectShare.Verdict.NotMeasurable)]    // the worker reported no tokens: never guess
    [InlineData(5000, 9000, 3, 1, ArchitectShare.Verdict.NotMeasurable)] // some worker calls without tokens would overstate the share
    [InlineData(2000, 8000, 2, 0, ArchitectShare.Verdict.Allow)]         // 20% <= 30%
    [InlineData(4000, 6000, 2, 0, ArchitectShare.Verdict.Exceeded)]      // 40% > 30%
    public void The_token_share_is_judged_only_on_reported_tokens(long architect, long worker, int workerCalls, int withoutTokens, ArchitectShare.Verdict expected)
    {
        Assert.Equal(expected, ArchitectShare.Evaluate(new TokenUse(architect, worker, workerCalls, withoutTokens), 0.30).Verdict);
    }

    [Fact]
    public void Planning_tokens_do_not_count_toward_the_share()
    {
        using var tmp = new TempDir();
        using var ledger = new SqliteLedger(Path.Combine(tmp.Path, "ledger.db"));
        ledger.StartRun(new RunRecord { Id = "r1", Strategy = "5", TaskId = "t", Repo = "demo", Started = DateTimeOffset.UtcNow });
        ledger.RecordLlmCall(new LlmCallRecord { RunId = "r1", Role = LlmRole.Architect, Model = "strong", InputTokens = 90_000, OutputTokens = 10_000, Purpose = "planning" });
        ledger.RecordLlmCall(new LlmCallRecord { RunId = "r1", Role = LlmRole.Architect, Model = "strong", InputTokens = 1_000, OutputTokens = 200, Purpose = CallPurposes.Review });
        ledger.RecordLlmCall(new LlmCallRecord { RunId = "r1", Role = LlmRole.Worker, Model = "cheap", InputTokens = 8_000, OutputTokens = 800 });

        var use = ledger.GetTokenUse("r1", ArchitectShare.ExcludedPurposes);

        Assert.Equal(new TokenUse(1_200, 8_800, 1, 0), use);
    }

    [Theory]
    [InlineData(3.00, null, 0.00, 3.00)]  // no per-run cap: the task budget
    [InlineData(3.00, 0.50, 0.00, 0.50)]  // per-run cap tighter
    [InlineData(3.00, 0.50, 1.00, 1.50)]  // per-run cap applies on top of earlier spend (planning, earlier runs)
    [InlineData(3.00, 5.00, 1.00, 3.00)]  // task budget tighter
    public void The_run_budget_is_the_tighter_of_the_task_budget_and_the_per_run_cap(double perTask, double? perRun, double spent, double expected)
    {
        var budget = new BudgetConfig { MaxUsdPerTask = (decimal)perTask, MaxUsdPerRun = perRun is { } r ? (decimal)r : null };

        Assert.Equal((decimal)expected, RunOrchestrator.BudgetLimit(budget, (decimal)spent));
    }
}
