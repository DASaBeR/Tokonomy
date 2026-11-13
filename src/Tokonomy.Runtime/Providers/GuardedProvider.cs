using Tokonomy.Core.Abstractions;

namespace Tokonomy.Runtime.Providers;

/// <summary>
/// Makes every way an architect call can fail look the same to the orchestrator: a timeout inside a provider
/// (<see cref="HttpClient"/> reports it as a <see cref="TaskCanceledException"/>, an <see cref="OperationCanceledException"/>
/// nobody asked for) or a <see cref="TimeoutException"/> becomes an <see cref="InvalidOperationException"/>, which every
/// caller already treats as "architect unavailable" and answers with its fallback. A cancellation the user asked for
/// (Ctrl+C, the budget guard) passes through unchanged.
/// </summary>
public sealed class GuardedProvider(IProvider inner) : IProvider
{
    public static IProvider? Wrap(IProvider? provider) => provider is null or GuardedProvider ? provider : new GuardedProvider(provider);

    public async Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct)
    {
        try
        {
            return await inner.CompleteAsync(request, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"the architect call timed out or was aborted by the provider ({ex.Message})", ex);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"the architect call timed out ({ex.Message})", ex);
        }
    }
}
