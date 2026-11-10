using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tokonomy.Llm.Providers;

namespace Tokonomy.Llm.Proxy;

/// <summary>
/// One metered upstream call that succeeded. Failed calls (4xx/5xx) are not recorded: providers do not bill them.
/// <see cref="InputTokens"/> is the total prompt size and includes cached and cache-write tokens.
/// </summary>
public sealed record UsageEvent(
    string Model, long InputTokens, long OutputTokens, long CachedTokens, decimal? Usd, bool Reported, int Status, long CacheWriteTokens = 0);

public sealed record ProxyOptions
{
    /// <summary>Upstream OpenAI-compatible base URL, e.g. https://openrouter.ai/api/v1.</summary>
    public required Uri Upstream { get; init; }

    /// <summary>The real provider key. Lives only in this process.</summary>
    public required string UpstreamKey { get; init; }

    /// <summary>Retries after HTTP 429, 503 or 529 from the upstream (rate limits are low on entry-level plans).</summary>
    public int MaxRetries { get; init; } = 4;

    /// <summary>First backoff delay when the upstream sends no Retry-After; doubles each attempt, with jitter.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for any single wait, including a Retry-After hint.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Test hook.</summary>
    public HttpMessageHandler? Handler { get; init; }
}

/// <summary>
/// Local metering proxy on 127.0.0.1. Workers authenticate with a per-run random token; the proxy swaps it for the
/// real key, forwards to the upstream, and records usage from the response (streaming included). Paths are
/// forwarded under the upstream base with a leading <c>/v1</c> stripped, so OpenAI-style
/// (<c>/v1/chat/completions</c>) and Anthropic-style (<c>/v1/messages</c>) clients both work.
/// </summary>
public sealed class MeteringProxy : IAsyncDisposable
{
    private const int MaxBufferedBody = 16 * 1024 * 1024;

    private static readonly HashSet<string> SkippedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Authorization", "x-api-key", "Content-Length", "Content-Type", "Connection", "Keep-Alive",
        "Transfer-Encoding", "Upgrade", "Proxy-Connection", "Accept-Encoding", "TE", "Trailer",
    };

    private static readonly HashSet<string> SkippedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Connection", "Keep-Alive", "Content-Length", "Content-Encoding", "Upgrade", "Trailer",
    };

    private readonly WebApplication _app;
    private readonly HttpClient _http;
    private readonly ProxyOptions _options;
    private readonly PriceTable _prices;
    private readonly Action<UsageEvent> _onUsage;
    private readonly Func<bool> _isBlocked;
    private readonly byte[] _tokenBytes;

    private MeteringProxy(WebApplication app, ProxyOptions options, PriceTable prices, Action<UsageEvent> onUsage, Func<bool> isBlocked)
    {
        _app = app;
        _options = options;
        _prices = prices;
        _onUsage = onUsage;
        _isBlocked = isBlocked;
        _http = new HttpClient(options.Handler ?? Tokonomy.Core.Net.ProxySettings.Current.CreateHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        Token = "trk_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        _tokenBytes = Encoding.UTF8.GetBytes(Token);
    }

    /// <summary>Bare origin, e.g. http://127.0.0.1:51234.</summary>
    public string Root { get; private set; } = "";

    /// <summary>OpenAI-style base URL ending in /v1.</summary>
    public string Url => Root + "/v1";

    public string Token { get; }

    /// <summary>Starts the proxy. <paramref name="isBlocked"/> returning true rejects new requests (budget exhausted).</summary>
    public static async Task<MeteringProxy> StartAsync(ProxyOptions options, PriceTable prices, Action<UsageEvent> onUsage, Func<bool>? isBlocked = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, 0);
            k.Limits.MaxRequestBodySize = MaxBufferedBody;
        });
        var app = builder.Build();
        var proxy = new MeteringProxy(app, options, prices, onUsage, isBlocked ?? (() => false));
        app.Map("/{**path}", proxy.HandleAsync);
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not report a listening address.");
        var address = addresses.Addresses.First();
        proxy.Root = address.Replace("[::1]", "127.0.0.1", StringComparison.Ordinal).TrimEnd('/');
        return proxy;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _http.Dispose();
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        if (!Authorized(ctx.Request))
        {
            await Error(ctx, StatusCodes.Status401Unauthorized, "invalid tokonomy proxy token");
            return;
        }

        if (_isBlocked())
        {
            await Error(ctx, StatusCodes.Status402PaymentRequired, "tokonomy budget exhausted for this run");
            return;
        }

        var path = ctx.Request.Path.Value ?? "/";
        if (path.StartsWith("/v1/", StringComparison.Ordinal) || path == "/v1")
        {
            path = path[3..];
        }

        var target = new Uri(_options.Upstream.AbsoluteUri.TrimEnd('/') + path + ctx.Request.QueryString.Value);

        string? requestModel = null;
        byte[]? requestBody = null;
        if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            (requestBody, requestModel) = PrepareBody(ms.ToArray(), path);
        }

        var forwarded = ctx.Request.Headers.Where(h => !SkippedRequestHeaders.Contains(h.Key)).ToList();
        var contentType = ctx.Request.ContentType;
        var method = new HttpMethod(ctx.Request.Method);

        // The body is buffered, so the request can be rebuilt and resent when the upstream throttles us.
        HttpRequestMessage Build()
        {
            var request = new HttpRequestMessage(method, target);
            if (requestBody is not null)
            {
                request.Content = new ByteArrayContent(requestBody);
                if (contentType is not null)
                {
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                }
            }

            foreach (var header in forwarded)
            {
                request.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value!);
            }

            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _options.UpstreamKey);
            if (path.EndsWith("/messages", StringComparison.Ordinal))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", _options.UpstreamKey);
            }

            return request;
        }

        HttpResponseMessage response;
        try
        {
            response = await SendWithRetryAsync(Build, ctx.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            await Error(ctx, StatusCodes.Status502BadGateway, "upstream unreachable: " + ex.Message);
            return;
        }

        using (response)
        {
            ctx.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (!SkippedResponseHeaders.Contains(header.Key))
                {
                    ctx.Response.Headers[header.Key] = header.Value.ToArray();
                }
            }

            var usage = new UsageAccumulator();
            var isSse = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
            await using var upstream = await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
            await Relay(upstream, ctx.Response, usage, isSse, ctx.RequestAborted);

            if (response.IsSuccessStatusCode && IsMeteredCall(ctx.Request.Method, path))
            {
                var model = requestModel ?? usage.Model ?? "unknown";
                var usd = usage.Seen
                    ? _prices.Cost(model, usage.InputTokens, usage.OutputTokens, usage.CachedTokens, usage.CacheWriteTokens)
                    : null;
                _onUsage(new UsageEvent(
                    model, usage.InputTokens, usage.OutputTokens, usage.CachedTokens, usd, usage.Seen, (int)response.StatusCode, usage.CacheWriteTokens));
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = build();
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!IsRetryable(response.StatusCode) || attempt >= _options.MaxRetries)
            {
                return response;
            }

            var delay = RetryDelay(response, attempt);
            response.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or (HttpStatusCode)529;

    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var max = _options.MaxRetryDelay;
        if (response.Headers.RetryAfter?.Delta is { } hinted)
        {
            return hinted > max ? max : hinted;
        }

        var backoff = TimeSpan.FromTicks(_options.RetryBaseDelay.Ticks << Math.Min(attempt, 10));
        var jitter = TimeSpan.FromTicks((long)(backoff.Ticks * (RandomNumberGenerator.GetInt32(0, 1000) / 1000.0) * 0.25));
        var total = backoff + jitter;
        return total > max ? max : total;
    }

    private static bool IsMeteredCall(string method, string path) =>
        HttpMethods.IsPost(method) &&
        (path.EndsWith("/chat/completions", StringComparison.Ordinal) || path.EndsWith("/messages", StringComparison.Ordinal) ||
         path.EndsWith("/completions", StringComparison.Ordinal) || path.EndsWith("/responses", StringComparison.Ordinal));

    /// <summary>Reads the model and forces usage reporting on OpenAI-style streaming requests.</summary>
    internal static (byte[] Body, string? Model) PrepareBody(byte[] body, string path)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject obj)
            {
                return (body, null);
            }

            var model = obj["model"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : null;
            var streaming = obj["stream"] is JsonValue st && st.TryGetValue<bool>(out var b) && b;
            if (streaming && path.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                var options = obj["stream_options"] as JsonObject ?? [];
                options["include_usage"] = true;
                obj["stream_options"] = options;
                return (Encoding.UTF8.GetBytes(obj.ToJsonString()), model);
            }

            return (body, model);
        }
        catch (System.Text.Json.JsonException)
        {
            return (body, null);
        }
    }

    private static async Task Relay(Stream upstream, HttpResponse response, UsageAccumulator usage, bool isSse, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var captured = new MemoryStream();
        var lineBuffer = new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        int read;
        while ((read = await upstream.ReadAsync(buffer, ct)) > 0)
        {
            await response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
            if (isSse)
            {
                await response.Body.FlushAsync(ct);
                var n = decoder.GetChars(buffer, 0, read, chars, 0);
                for (var i = 0; i < n; i++)
                {
                    if (chars[i] == '\n')
                    {
                        usage.AddSseLine(lineBuffer.ToString().TrimEnd('\r'));
                        lineBuffer.Clear();
                    }
                    else
                    {
                        lineBuffer.Append(chars[i]);
                    }
                }
            }
            else if (captured.Length < MaxBufferedBody)
            {
                captured.Write(buffer, 0, read);
            }
        }

        if (isSse && lineBuffer.Length > 0)
        {
            usage.AddSseLine(lineBuffer.ToString());
        }
        else if (!isSse && captured.Length > 0)
        {
            usage.AddJson(Encoding.UTF8.GetString(captured.GetBuffer(), 0, (int)captured.Length));
        }
    }

    private bool Authorized(HttpRequest request)
    {
        string? presented = null;
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            presented = auth[7..].Trim();
        }
        else if (request.Headers.TryGetValue("x-api-key", out var key))
        {
            presented = key.ToString().Trim();
        }

        return presented is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _tokenBytes);
    }

    private static Task Error(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var json = new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = "tokonomy_proxy" } };
        return ctx.Response.WriteAsync(json.ToJsonString());
    }
}
