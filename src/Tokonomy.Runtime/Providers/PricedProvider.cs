using Tokonomy.Core.Abstractions;
using Tokonomy.Llm.Providers;
using Tokonomy.Runtime.Orchestration;

namespace Tokonomy.Runtime.Providers;

/// <summary>
/// Prices architect calls from <c>prices.yaml</c> when the provider reports tokens but no dollar figure (the Antigravity
/// and Codex CLIs on a subscription). Every caller (planning, runs, the benchmark judge) then sees an API-equivalent,
/// estimated cost instead of $0. A response that already carries a cost passes through unchanged.
/// </summary>
public sealed class PricedProvider(IProvider inner, PriceTable prices) : IProvider
{
    public async Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await inner.CompleteAsync(request, ct);
        if (response.Usd is not null)
        {
            return response;
        }

        // The price table is keyed by the model the user configured; the answer may name it differently.
        var usd = UsagePricing.ReportedCost(prices, response.Model, response.InputTokens, response.OutputTokens, response.CachedTokens)
                  ?? UsagePricing.ReportedCost(prices, request.Model, response.InputTokens, response.OutputTokens, response.CachedTokens);
        return usd is null ? response : response with { Usd = usd, Estimated = true };
    }
}
