using System.ClientModel;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using OpenAI;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;

namespace Tokonomy.Llm.Providers;

public sealed class MissingApiKeyException(string variable)
    : Exception($"Environment variable '{variable}' is not set. Export your provider API key under that name.")
{
    public string Variable { get; } = variable;
}

/// <summary>
/// Architect access over any OpenAI-compatible endpoint (OpenRouter by default) via Microsoft.Extensions.AI.
/// The API key is read from the environment variable named in config and never written anywhere.
/// </summary>
public sealed class OpenAiCompatibleProvider : IProvider, IDisposable
{
    private readonly OpenAIClient _client;
    private readonly PriceTable _prices;
    private readonly ConcurrentDictionary<string, IChatClient> _chatClients = new(StringComparer.Ordinal);

    public OpenAiCompatibleProvider(ProviderConfig config, PriceTable prices, Func<string, string?>? getEnv = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        getEnv ??= Environment.GetEnvironmentVariable;
        var key = getEnv(config.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new MissingApiKeyException(config.ApiKeyEnv);
        }

        _client = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = new Uri(config.BaseUrl) });
        _prices = prices;
    }

    public async Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chat = _chatClients.GetOrAdd(request.Model, m => _client.GetChatClient(m).AsIChatClient());

        var options = new ChatOptions();
        if (request.JsonOutput)
        {
            options.ResponseFormat = ChatResponseFormat.Json;
        }

        ChatMessage[] messages =
        [
            new(ChatRole.System, request.System),
            new(ChatRole.User, request.User),
        ];

        var response = await chat.GetResponseAsync(messages, options, ct);
        var input = response.Usage?.InputTokenCount ?? 0;
        var output = response.Usage?.OutputTokenCount ?? 0;
        var cached = response.Usage?.CachedInputTokenCount ?? 0;

        return new ProviderResponse(
            response.Text,
            response.ModelId ?? request.Model,
            input,
            output,
            cached,
            _prices.Cost(request.Model, input, output, cached));
    }

    public void Dispose()
    {
        foreach (var chat in _chatClients.Values)
        {
            chat.Dispose();
        }
    }
}
