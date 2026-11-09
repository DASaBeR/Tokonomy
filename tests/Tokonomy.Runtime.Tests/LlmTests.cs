using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Tokonomy.Core.Abstractions;
using Tokonomy.Llm.Architect;
using Tokonomy.Llm.Providers;
using Tokonomy.Llm.Proxy;

namespace Tokonomy.Runtime.Tests;

/// <summary>Fake upstream: records requests and answers like an OpenAI-compatible or Anthropic endpoint.</summary>
internal sealed class FakeUpstream(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status, string body = "{\"error\":{\"message\":\"nope\"}}", TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }

        return response;
    }

    public static HttpResponseMessage Sse(string events)
    {
        var content = new StringContent(events, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

public class MeteringProxyTests
{
    private static readonly PriceTable Prices = new(new Dictionary<string, ModelPrice>
    {
        ["cheap"] = new() { Input = 1m, Output = 2m, CachedInput = 0.5m },
    });

    private static async Task<(MeteringProxy Proxy, List<UsageEvent> Usage, FakeUpstream Upstream)> Start(
        Func<HttpRequestMessage, string, HttpResponseMessage> respond, Func<bool>? blocked = null, int maxRetries = 4)
    {
        var usage = new List<UsageEvent>();
        var upstream = new FakeUpstream(respond);
        var proxy = await MeteringProxy.StartAsync(
            new ProxyOptions
            {
                Upstream = new Uri("https://upstream.example/api/v1"),
                UpstreamKey = "sk-real-key",
                Handler = upstream,
                MaxRetries = maxRetries,
                RetryBaseDelay = TimeSpan.Zero,
            },
            Prices,
            e => { lock (usage) { usage.Add(e); } },
            blocked);
        return (proxy, usage, upstream);
    }

    private static HttpRequestMessage Post(MeteringProxy proxy, string path, string body, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, proxy.Root + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (token is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return req;
    }

    [Fact]
    public async Task Rejects_requests_without_the_run_token()
    {
        var (proxy, usage, upstream) = await Start((_, _) => FakeUpstream.Json("{}"));
        await using (proxy)
        {
            using var http = new HttpClient();
            var wrong = await http.SendAsync(Post(proxy, "/v1/chat/completions", "{}", "trk_wrong"));
            var none = await http.SendAsync(Post(proxy, "/v1/chat/completions", "{}", null));

            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
            Assert.Empty(upstream.Requests);
            Assert.Empty(usage);
        }
    }

    [Fact]
    public async Task Swaps_token_for_real_key_and_meters_json_response()
    {
        var (proxy, usage, upstream) = await Start((_, _) => FakeUpstream.Json(
            """{"model":"cheap","choices":[],"usage":{"prompt_tokens":1000,"completion_tokens":500,"prompt_tokens_details":{"cached_tokens":400}}}"""));
        await using (proxy)
        {
            using var http = new HttpClient();
            var response = await http.SendAsync(Post(proxy, "/v1/chat/completions", """{"model":"cheap","messages":[]}""", proxy.Token));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var (req, _) = Assert.Single(upstream.Requests);
            Assert.Equal("https://upstream.example/api/v1/chat/completions", req.RequestUri!.ToString());
            Assert.Equal("Bearer sk-real-key", req.Headers.Authorization!.ToString());

            var e = Assert.Single(usage);
            Assert.Equal(("cheap", 1000L, 500L, 400L), (e.Model, e.InputTokens, e.OutputTokens, e.CachedTokens));
            // 600 uncached * $1/M + 400 cached * $0.5/M + 500 out * $2/M
            Assert.Equal(0.0018m, e.Usd);
            Assert.True(e.Reported);
        }
    }

    [Fact]
    public async Task Forces_usage_on_openai_streams_and_meters_sse()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
                  "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}\n\n" +
                  "data: [DONE]\n\n";
        var (proxy, usage, upstream) = await Start((_, _) => FakeUpstream.Sse(sse));
        await using (proxy)
        {
            using var http = new HttpClient();
            var response = await http.SendAsync(Post(proxy, "/v1/chat/completions", """{"model":"cheap","stream":true}""", proxy.Token));
            var text = await response.Content.ReadAsStringAsync();

            Assert.Contains("[DONE]", text, StringComparison.Ordinal);
            Assert.Contains("\"include_usage\":true", upstream.Requests[0].Body, StringComparison.Ordinal);
            var e = Assert.Single(usage);
            Assert.Equal((10L, 5L), (e.InputTokens, e.OutputTokens));
        }
    }

    [Fact]
    public async Task Meters_anthropic_messages_stream()
    {
        var sse = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"model\":\"cheap\",\"usage\":{\"input_tokens\":20,\"cache_read_input_tokens\":80,\"output_tokens\":1}}}\n\n" +
                  "event: message_delta\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":42}}\n\n";
        var (proxy, usage, upstream) = await Start((_, _) => FakeUpstream.Sse(sse));
        await using (proxy)
        {
            using var http = new HttpClient();
            var req = Post(proxy, "/v1/messages", """{"model":"cheap","stream":true}""", null);
            req.Headers.Add("x-api-key", proxy.Token);
            await (await http.SendAsync(req)).Content.ReadAsStringAsync();

            Assert.Equal("https://upstream.example/api/v1/messages", upstream.Requests[0].Request.RequestUri!.ToString());
            Assert.Equal("sk-real-key", upstream.Requests[0].Request.Headers.GetValues("x-api-key").Single());
            var e = Assert.Single(usage);
            Assert.Equal((100L, 42L, 80L), (e.InputTokens, e.OutputTokens, e.CachedTokens));
        }
    }

    [Fact]
    public async Task Blocks_new_requests_when_budget_exhausted()
    {
        var (proxy, _, upstream) = await Start((_, _) => FakeUpstream.Json("{}"), blocked: () => true);
        await using (proxy)
        {
            using var http = new HttpClient();
            var response = await http.SendAsync(Post(proxy, "/v1/chat/completions", "{}", proxy.Token));

            Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
            Assert.Empty(upstream.Requests);
        }
    }

    [Fact]
    public async Task Retries_throttled_requests_resends_the_body_and_meters_only_the_success()
    {
        var calls = 0;
        var (proxy, usage, upstream) = await Start((_, _) => ++calls <= 2
            ? FakeUpstream.Status(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.Zero)
            : FakeUpstream.Json("""{"model":"cheap","usage":{"prompt_tokens":10,"completion_tokens":5}}"""));
        await using (proxy)
        {
            using var http = new HttpClient();
            var response = await http.SendAsync(Post(proxy, "/v1/chat/completions", """{"model":"cheap","messages":[{"role":"user","content":"hi"}]}""", proxy.Token));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(3, upstream.Requests.Count);
            Assert.Single(upstream.Requests.Select(r => r.Body).Distinct());
            Assert.Contains("\"hi\"", upstream.Requests[2].Body, StringComparison.Ordinal);
            Assert.Equal((10L, 5L), (Assert.Single(usage).InputTokens, usage[0].OutputTokens));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData((HttpStatusCode)529)]
    public async Task Retries_overload_responses_too(HttpStatusCode status)
    {
        var calls = 0;
        var (proxy, _, upstream) = await Start((_, _) => ++calls == 1 ? FakeUpstream.Status(status) : FakeUpstream.Json("{}"));
        await using (proxy)
        {
            using var http = new HttpClient();
            var response = await http.SendAsync(Post(proxy, "/v1/chat/completions", "{}", proxy.Token));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, upstream.Requests.Count);
        }
    }

    [Fact]
    public async Task Gives_up_after_max_retries_passes_the_error_through_and_does_not_meter()
    {
        var (proxy, usage, upstream) = await Start(
            (_, _) => FakeUpstream.Status(HttpStatusCode.TooManyRequests, """{"error":{"message":"slow down"}}""", TimeSpan.Zero), maxRetries: 2);
        await using (proxy)
        {
            using var http = new HttpClient();
            var response = await http.SendAsync(Post(proxy, "/v1/chat/completions", """{"model":"cheap"}""", proxy.Token));

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Contains("slow down", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(3, upstream.Requests.Count);
            Assert.Empty(usage);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Failed_calls_are_not_retried_or_metered(HttpStatusCode status)
    {
        var (proxy, usage, upstream) = await Start((_, _) => FakeUpstream.Status(status));
        await using (proxy)
        {
            using var http = new HttpClient();
            var response = await http.SendAsync(Post(proxy, "/v1/chat/completions", """{"model":"cheap"}""", proxy.Token));

            Assert.Equal(status, response.StatusCode);
            Assert.Single(upstream.Requests);
            Assert.Empty(usage);
        }
    }

    [Fact]
    public async Task Prices_cache_writes_and_long_context_requests()
    {
        var prices = new PriceTable(new Dictionary<string, ModelPrice>
        {
            ["claude"] = new()
            {
                Input = 0.10m, CachedInput = 0.01m, CacheWrite = 0.20m, Output = 0.50m,
                AboveInputTokens = 100_000,
                Above = new ModelPriceTier { Input = 0.50m, CachedInput = 0.05m, CacheWrite = 1.00m, Output = 2.50m },
            },
        });
        var usage = new List<UsageEvent>();
        var upstream = new FakeUpstream((_, _) => FakeUpstream.Json(
            """{"model":"claude","usage":{"input_tokens":50000,"cache_read_input_tokens":40000,"cache_creation_input_tokens":20000,"output_tokens":1000}}"""));
        await using var proxy = await MeteringProxy.StartAsync(
            new ProxyOptions { Upstream = new Uri("https://upstream.example/v1"), UpstreamKey = "k", Handler = upstream }, prices, usage.Add);

        using var http = new HttpClient();
        await http.SendAsync(Post(proxy, "/v1/messages", """{"model":"claude"}""", proxy.Token));

        var e = Assert.Single(usage);
        // 110k total input is above the 100k threshold, so the "above" rates apply to everything:
        // 50k uncached*0.5 + 40k cached*0.05 + 20k write*1.0 + 1k out*2.5 = 25,000 + 2,000 + 20,000 + 2,500 per million
        Assert.Equal((110_000L, 40_000L, 20_000L), (e.InputTokens, e.CachedTokens, e.CacheWriteTokens));
        Assert.Equal(0.0495m, e.Usd);
    }

    [Fact]
    public async Task Unpriced_model_reports_tokens_without_cost()
    {
        var (proxy, usage, _) = await Start((_, _) => FakeUpstream.Json("""{"usage":{"prompt_tokens":1,"completion_tokens":1}}"""));
        await using (proxy)
        {
            using var http = new HttpClient();
            await http.SendAsync(Post(proxy, "/v1/chat/completions", """{"model":"mystery"}""", proxy.Token));

            Assert.Null(Assert.Single(usage).Usd);
        }
    }
}

public class UsageAccumulatorTests
{
    [Fact]
    public void Reads_anthropic_cache_writes_and_counts_them_in_input()
    {
        var usage = new UsageAccumulator();
        usage.AddJson("""{"usage":{"input_tokens":10,"cache_read_input_tokens":20,"cache_creation_input_tokens":30,"output_tokens":4}}""");

        Assert.Equal((60L, 20L, 30L, 4L), (usage.InputTokens, usage.CachedTokens, usage.CacheWriteTokens, usage.OutputTokens));
    }

    [Theory]
    [InlineData("""{"usage":{"prompt_tokens":100,"completion_tokens":1,"prompt_tokens_details":{"cached_tokens":10,"cache_write_tokens":30}}}""")]
    [InlineData("""{"usage":{"prompt_tokens":100,"completion_tokens":1,"prompt_tokens_details":{"cached_tokens":10},"cache_creation_input_tokens":30}}""")]
    public void Reads_openai_style_cache_writes_under_either_name(string json)
    {
        var usage = new UsageAccumulator();
        usage.AddJson(json);

        Assert.Equal((100L, 10L, 30L), (usage.InputTokens, usage.CachedTokens, usage.CacheWriteTokens));
    }

    [Fact]
    public void No_usage_object_means_nothing_seen()
    {
        var usage = new UsageAccumulator();
        usage.AddJson("""{"choices":[]}""");

        Assert.False(usage.Seen);
    }
}

internal sealed class ScriptedProvider(params string[] answers) : IProvider
{
    private int _next;

    public List<ProviderRequest> Requests { get; } = [];

    public Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var text = answers[Math.Min(_next++, answers.Length - 1)];
        return Task.FromResult(new ProviderResponse(text, request.Model, 1000, 200, 0, 0.01m));
    }
}

public class PlannerTests
{
    private const string Good = """
        ```json
        {"units":[{"id":"U1","title":"Limiter","spec":"Add RateLimiter","depends_on":[]},
                  {"id":"U2","title":"Wire","spec":"Use it in /login","depends_on":["U1"]}],
         "rules":[{"id":"R1","text":"no module-level state"}], "dont":["add deps"]}
        ```
        """;

    [Fact]
    public async Task Parses_fenced_json_contract()
    {
        var provider = new ScriptedProvider(Good);

        var result = await new Planner(provider, "strong").PlanAsync("Add rate limiting", "files...", default);

        Assert.Equal(["U1", "U2"], result.Contract.Units.Select(u => u.Id));
        Assert.Equal(["U1"], result.Contract.Units[1].DependsOn);
        Assert.Equal("no module-level state", result.Contract.Rules.Single().Text);
        Assert.Single(result.Calls);
        Assert.True(provider.Requests[0].JsonOutput);
    }

    [Fact]
    public async Task Reasks_once_with_validation_errors()
    {
        var cyclic = """{"units":[{"id":"A","title":"a","spec":"a","depends_on":["B"]},{"id":"B","title":"b","spec":"b","depends_on":["A"]}]}""";
        var provider = new ScriptedProvider(cyclic, Good);

        var result = await new Planner(provider, "strong").PlanAsync("task", "", default);

        Assert.Equal(2, result.Calls.Count);
        Assert.Contains("cycle", provider.Requests[1].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_after_two_invalid_answers_and_reports_calls()
    {
        var provider = new ScriptedProvider("I cannot help with that.");

        var ex = await Assert.ThrowsAsync<PlanningException>(() => new Planner(provider, "strong").PlanAsync("task", "", default));

        Assert.Equal(2, ex.Calls.Count);
    }
}
