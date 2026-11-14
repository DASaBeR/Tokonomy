using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Model;
using Tokonomy.Llm.Providers;

namespace Tokonomy.Runtime.Orchestration;

/// <summary>
/// Turns reported token usage into a cost and a cost source, the same way for worker and architect calls, so usage
/// without a reported dollar figure is priced from <c>prices.yaml</c> instead of silently counting as free.
/// </summary>
public static class UsagePricing
{
    /// <summary>
    /// Cost of usage a CLI reported itself. These CLIs report cache reads separately from fresh input, while the price
    /// table expects the total prompt size, so the two are added. Null when the model has no configured price.
    /// </summary>
    public static decimal? ReportedCost(PriceTable prices, string model, long inputTokens, long outputTokens, long cachedTokens)
    {
        ArgumentNullException.ThrowIfNull(prices);
        return prices.Cost(model, inputTokens + cachedTokens, outputTokens, cachedTokens);
    }

    /// <summary>
    /// The amount and source to record for an architect call. A provider-reported amount is used as is. Otherwise the
    /// tokens are priced from the table (estimated); if that is impossible the call is recorded as <see cref="CostSource.Unknown"/>.
    /// </summary>
    public static (decimal Usd, CostSource Source) ForArchitect(ProviderResponse response, PriceTable prices)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Usd is { } known)
        {
            return (known, response.Estimated ? CostSource.Estimated : CostSource.Metered);
        }

        return ReportedCost(prices, response.Model, response.InputTokens, response.OutputTokens, response.CachedTokens) is { } priced
            ? (priced, CostSource.Estimated)
            : (0m, CostSource.Unknown);
    }
}
