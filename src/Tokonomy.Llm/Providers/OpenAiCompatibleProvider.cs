using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Net;

namespace Tokonomy.Llm.Providers;

public sealed class MissingApiKeyException(string variable)
    : Exception($"Environment variable '{variable}' is not set. Export your provider API key under that name.")
{
    public string Variable { get; } = variable;
}

/// <summary>
/// Architect access over any OpenAI-compatible endpoint (OpenRouter, DeepSeek, OpenAI, Gemini, or a custom address) via
/// Microsoft.Extensions.AI. The API key is read from the environment variable named in config and never written anywhere.
/// <para>
/// The answer is streamed, which gives live progress and lets a call that goes silent (a dead connection) be ended and asked
/// once more. A server that cannot stream, or ignores the request to, is detected on the first call and used the old way
/// (one reply) from then on.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleProvider : IProvider, IDisposable
{
    /// <summary>
    /// Silence tolerated before the call is ended. Longer than for the CLI providers: some models think for minutes before the
    /// first token and the SDK hides the server's keep-alive comments.
    /// </summary>
    internal static readonly TimeSpan DefaultIdle = TimeSpan.FromMinutes(5);

    private const int Attempts = 2;

    private readonly OpenAIClient _client;
    private readonly HttpClient? _http;
    private readonly PriceTable _prices;
    private readonly TimeSpan _idle = DefaultIdle;
    private readonly ConcurrentDictionary<string, IChatClient> _chatClients = new(StringComparer.Ordinal);
    private volatile bool _streamingUnsupported;

    public OpenAiCompatibleProvider(ProviderConfig config, PriceTable prices, Func<string, string?>? getEnv = null, ProxySettings? proxy = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        getEnv ??= Environment.GetEnvironmentVariable;
        var key = getEnv(config.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new MissingApiKeyException(config.ApiKeyEnv);
        }

        var options = new OpenAIClientOptions { Endpoint = new Uri(config.BaseUrl) };
        proxy ??= ProxySettings.Current;
        if (proxy.IsActive)
        {
            _http = new HttpClient(proxy.CreateHandler(), disposeHandler: true) { Timeout = TimeSpan.FromMinutes(30) };
            options.Transport = new HttpClientPipelineTransport(_http);
        }

        _client = new OpenAIClient(new ApiKeyCredential(key), options);
        _prices = prices;
    }

    /// <summary>For tests: talk to a fake server through <paramref name="handler"/> and use a short idle limit.</summary>
    internal OpenAiCompatibleProvider(ProviderConfig config, PriceTable prices, string apiKey, HttpMessageHandler handler, TimeSpan idle)
    {
        ArgumentNullException.ThrowIfNull(config);
        _http = new HttpClient(handler, disposeHandler: false);
        _client = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions
        {
            Endpoint = new Uri(config.BaseUrl),
            Transport = new HttpClientPipelineTransport(_http),
        });
        _prices = prices;
        _idle = idle;
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

        if (!_streamingUnsupported && await TryStreamAsync(chat, request, messages, options, ct) is { } streamed)
        {
            return streamed;
        }

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

    /// <summary>
    /// Streams the answer. Returns null when the server does not stream (it rejects the request, or answers without any
    /// stream events), after which the caller asks the ordinary way and later calls skip streaming.
    /// </summary>
    private async Task<ProviderResponse?> TryStreamAsync(
        IChatClient chat, ProviderRequest request, ChatMessage[] messages, ChatOptions options, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var idleStop = new CancellationTokenSource(_idle);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idleStop.Token);
            var text = new StringBuilder();
            UsageDetails? usage = null;
            string? modelId = null;
            var updates = 0;

            try
            {
                await foreach (var update in chat.GetStreamingResponseAsync(messages, options, linked.Token))
                {
                    idleStop.CancelAfter(_idle);
                    updates++;
                    modelId ??= update.ModelId;
                    var before = text.Length;
                    text.Append(update.Text);
                    foreach (var content in update.Contents)
                    {
                        if (content is UsageContent u)
                        {
                            usage = u.Details;
                        }
                    }

                    if (text.Length > before)
                    {
                        request.OnProgress?.Invoke(new ProviderProgress(text.Length, Retrying: attempt > 1));
                    }
                }
            }
            catch (OperationCanceledException) when (idleStop.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                if (attempt >= Attempts)
                {
                    throw new InvalidOperationException(
                        $"The model stopped sending data for {_idle.TotalMinutes:0.#} minutes, twice. The connection looks dead: " +
                        "check your network or proxy (`tokonomy proxy test`) and try again.");
                }

                request.OnProgress?.Invoke(new ProviderProgress(0, Retrying: true));
                continue;
            }
            catch (Exception ex) when (updates == 0 &&
                                       ex is ClientResultException { Status: 400 or 404 or 405 or 415 or 422 or 501 } or JsonException or FormatException)
            {
                _streamingUnsupported = true;
                return null;
            }

            if (updates == 0)
            {
                // A 200 with no stream events: the server ignored the request to stream.
                _streamingUnsupported = true;
                return null;
            }

            // Not every server reports usage while streaming. Say so (estimated) rather than record a free call.
            var estimated = usage?.InputTokenCount is null && usage?.OutputTokenCount is null;
            long input;
            long output;
            long cached;
            if (estimated)
            {
                input = (request.System.Length + request.User.Length) / 4;
                output = text.Length / 4;
                cached = 0;
            }
            else
            {
                input = usage?.InputTokenCount ?? 0;
                output = usage?.OutputTokenCount ?? 0;
                cached = usage?.CachedInputTokenCount ?? 0;
            }

            return new ProviderResponse(
                text.ToString(), modelId ?? request.Model, input, output, cached,
                _prices.Cost(request.Model, input, output, cached), Estimated: estimated);
        }
    }

    public void Dispose()
    {
        foreach (var chat in _chatClients.Values)
        {
            chat.Dispose();
        }

        _http?.Dispose();
    }
}
