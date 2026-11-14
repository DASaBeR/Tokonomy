using System.Net;
using System.Text;
using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Core.Net;
using Tokonomy.Llm.Providers;
using Tokonomy.Runtime.Process;
using Tokonomy.Runtime.Providers;

namespace Tokonomy.Runtime.Tests;

/// <summary>A runner that plays back prepared outcomes and feeds prepared stdout lines to the caller's listener.</summary>
internal sealed class ScriptedRunner(params (string[] Lines, ProcessOutcome Outcome)[] script) : IProcessRunner
{
    private int _next;

    public List<ProcessSpec> Specs { get; } = [];

    public Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        Specs.Add(spec);
        var (lines, outcome) = script[Math.Min(_next++, script.Length - 1)];
        foreach (var line in lines)
        {
            spec.OnStdoutLine?.Invoke(line);
        }

        return Task.FromResult(outcome);
    }

    public static ProcessOutcome Done(string stdout) =>
        new() { ExitCode = 0, TimedOut = false, Duration = TimeSpan.Zero, Stdout = stdout, Stderr = "" };

    public static ProcessOutcome Silent() =>
        new() { ExitCode = -1, TimedOut = true, IdledOut = true, Duration = TimeSpan.Zero, Stdout = "", Stderr = "" };
}

public class ProcessRunnerStreamingTests
{
    private static string[] Shell(string windows, string unix) => OperatingSystem.IsWindows()
        ? ["powershell", "-NoProfile", "-Command", windows]
        : ["sh", "-c", unix];

    [Fact]
    public async Task Delivers_each_stdout_line_as_it_arrives_including_a_last_line_without_newline()
    {
        var lines = new List<string>();
        var spec = new ProcessSpec
        {
            Args = Shell("[Console]::Out.Write(\"one`ntwo`nthree\")", "printf 'one\\ntwo\\nthree'"),
            WorkingDirectory = Path.GetTempPath(),
            OnStdoutLine = lines.Add,
        };

        var outcome = await new ProcessRunner().RunAsync(spec, default);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["one", "two", "three"], lines);
    }

    [Fact]
    public async Task A_process_that_goes_silent_is_ended_and_reported_as_idled_out()
    {
        var spec = new ProcessSpec
        {
            Args = Shell("Start-Sleep -Seconds 60", "sleep 60"),
            WorkingDirectory = Path.GetTempPath(),
            IdleTimeout = TimeSpan.FromSeconds(1),
        };

        var outcome = await new ProcessRunner().RunAsync(spec, default);

        Assert.True(outcome.IdledOut);
        Assert.True(outcome.TimedOut);
        Assert.True(outcome.Duration < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_process_that_keeps_printing_is_not_idled_out()
    {
        var spec = new ProcessSpec
        {
            Args = Shell("1..4 | ForEach-Object { Write-Output $_; Start-Sleep -Milliseconds 600 }", "for i in 1 2 3 4; do echo $i; sleep 0.6; done"),
            WorkingDirectory = Path.GetTempPath(),
            IdleTimeout = TimeSpan.FromSeconds(10),
        };

        var outcome = await new ProcessRunner().RunAsync(spec, default);

        Assert.False(outcome.IdledOut);
        Assert.True(outcome.Succeeded);
    }
}

public class CliProviderStreamingTests
{
    private const string ClaudeResult =
        """{"type":"result","is_error":false,"result":"{\"units\":[]}","total_cost_usd":0.05,"modelUsage":{"claude-opus-5-5":{"inputTokens":100,"outputTokens":40,"cacheReadInputTokens":0,"cacheCreationInputTokens":0,"costUSD":0.05}}}""";

    private const string ClaudeDelta = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"12345678"}}}""";

    [Fact]
    public async Task Claude_reports_progress_from_text_and_thinking_deltas()
    {
        var thinking = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"abcd"}}}""";
        var runner = new ScriptedRunner(([thinking, ClaudeDelta, "{\"type\":\"system\"}"], ScriptedRunner.Done(ClaudeResult)));
        var seen = new List<ProviderProgress>();

        await new ClaudeCliProvider(runner).CompleteAsync(
            new ProviderRequest("claude-opus-5-5", "s", "u") { OnProgress = seen.Add }, default);

        Assert.Equal([4L, 12L], seen.Select(p => p.OutputChars));
        Assert.All(seen, p => Assert.False(p.Retrying));
        Assert.Contains("stream-json", runner.Specs[0].Args);
        Assert.Contains("--include-partial-messages", runner.Specs[0].Args);
        Assert.NotNull(runner.Specs[0].IdleTimeout);
    }

    [Fact]
    public async Task A_silent_call_is_asked_again_once_and_the_retry_is_reported()
    {
        var runner = new ScriptedRunner(([], ScriptedRunner.Silent()), ([ClaudeDelta], ScriptedRunner.Done(ClaudeResult)));
        var seen = new List<ProviderProgress>();

        var response = await new ClaudeCliProvider(runner).CompleteAsync(
            new ProviderRequest("claude-opus-5-5", "s", "u") { OnProgress = seen.Add }, default);

        Assert.Equal("{\"units\":[]}", response.Text);
        Assert.Equal(2, runner.Specs.Count);
        Assert.Contains(seen, p => p is { Retrying: true, OutputChars: 0 });
        Assert.Contains(seen, p => p is { Retrying: true, OutputChars: > 0 });
    }

    [Fact]
    public async Task Two_silent_calls_in_a_row_fail_with_a_clear_message()
    {
        var runner = new ScriptedRunner(([], ScriptedRunner.Silent()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ClaudeCliProvider(runner).CompleteAsync(new ProviderRequest("claude-opus-5-5", "s", "u"), default));

        Assert.Equal(2, runner.Specs.Count);
        Assert.Contains("stopped sending data", ex.Message, StringComparison.Ordinal);
        Assert.Contains("proxy test", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Antigravity_counts_text_delta_characters_only()
    {
        Assert.Equal(5, AgyCliProvider.StreamedChars("""{"event":"step_update","step_update":{"step_index":1,"state":"ACTIVE","text_delta":"hello"}}"""));
        Assert.Equal(0, AgyCliProvider.StreamedChars("""{"event":"init","init":{"model":"m"}}"""));
        Assert.Equal(0, AgyCliProvider.StreamedChars("text_delta but not json"));
    }

    [Fact]
    public void Codex_counts_the_text_of_completed_items_only()
    {
        Assert.Equal(11, CodexCliProvider.StreamedChars("""{"type":"item.completed","item":{"id":"i","type":"agent_message","text":"hello world"}}"""));
        Assert.Equal(0, CodexCliProvider.StreamedChars("""{"type":"turn.started"}"""));
        Assert.Equal(0, CodexCliProvider.StreamedChars("2026 ERROR item.completed {broken"));
    }

    [Fact]
    public async Task Antigravity_and_Codex_use_the_same_idle_retry()
    {
        var agy = new ScriptedRunner(([], ScriptedRunner.Silent()));
        var codex = new ScriptedRunner(([], ScriptedRunner.Silent()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AgyCliProvider(agy, command: Environment.ProcessPath!, scratchRoot: Path.Combine(Path.GetTempPath(), "tk-agy-" + Guid.NewGuid().ToString("N")))
                .CompleteAsync(new ProviderRequest("m", "s", "u"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CodexCliProvider(codex).CompleteAsync(new ProviderRequest("m", "s", "u"), default));

        Assert.Equal(2, agy.Specs.Count);
        Assert.Equal(2, codex.Specs.Count);
    }
}

public class ApiProviderStreamingTests
{
    private static readonly ProviderConfig Config = new() { BaseUrl = "https://example.test/v1", ApiKeyEnv = "UNUSED" };

    private static readonly PriceTable Prices = new(new Dictionary<string, ModelPrice>
    {
        ["m"] = new() { Input = 1m, Output = 2m, CachedInput = 0.5m },
    });

    private static string Chunk(string content, string? usage = null) =>
        "data: " + (usage is null
            ? $$"""{"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"{{content}}"},"finish_reason":null}]}"""
            : $$"""{"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{{usage}}}""") + "\n\n";

    private const string Usage = """{"prompt_tokens":100,"completion_tokens":40,"total_tokens":140}""";

    private const string Whole =
        """{"id":"x","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"whole"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""";

    private static OpenAiCompatibleProvider Provider(HttpMessageHandler handler, TimeSpan? idle = null) =>
        new(Config, Prices, "key", handler, idle ?? TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Streams_the_answer_reports_progress_and_uses_the_reported_usage()
    {
        var upstream = new FakeUpstream((_, _) => FakeUpstream.Sse(Chunk("Hel") + Chunk("lo!") + Chunk("", Usage) + "data: [DONE]\n\n"));
        var seen = new List<long>();

        var response = await Provider(upstream).CompleteAsync(
            new ProviderRequest("m", "sys", "user") { OnProgress = p => seen.Add(p.OutputChars) }, default);

        Assert.Equal("Hello!", response.Text);
        Assert.Equal(100, response.InputTokens);
        Assert.Equal(40, response.OutputTokens);
        Assert.False(response.Estimated);
        Assert.NotNull(response.Usd);
        Assert.Equal([3L, 6L], seen);
        Assert.Contains("\"stream\":true", upstream.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("include_usage", upstream.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_streamed_usage_the_call_is_estimated_not_free()
    {
        var upstream = new FakeUpstream((_, _) => FakeUpstream.Sse(Chunk("a long enough answer text") + "data: [DONE]\n\n"));

        var response = await Provider(upstream).CompleteAsync(new ProviderRequest("m", "system prompt", "user prompt"), default);

        Assert.True(response.Estimated);
        Assert.True(response.OutputTokens > 0);
        Assert.True(response.InputTokens > 0);
    }

    [Fact]
    public async Task A_server_that_ignores_streaming_is_used_the_ordinary_way_from_then_on()
    {
        var upstream = new FakeUpstream((_, body) => body.Contains("\"stream\":true", StringComparison.Ordinal)
            ? FakeUpstream.Json(Whole)   // 200, but not an event stream
            : FakeUpstream.Json(Whole));

        var provider = Provider(upstream);
        var first = await provider.CompleteAsync(new ProviderRequest("m", "s", "u"), default);
        var requestsAfterFirst = upstream.Requests.Count;
        var second = await provider.CompleteAsync(new ProviderRequest("m", "s", "u"), default);

        Assert.Equal("whole", first.Text);
        Assert.Equal("whole", second.Text);
        Assert.Equal(10, first.InputTokens);
        Assert.Equal(2, requestsAfterFirst);                       // one streaming try, one ordinary call
        Assert.Equal(requestsAfterFirst + 1, upstream.Requests.Count); // the second call went straight to the ordinary way
        Assert.DoesNotContain("\"stream\":true", upstream.Requests[^1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_rejects_the_stream_option_falls_back()
    {
        var upstream = new FakeUpstream((_, body) => body.Contains("\"stream\":true", StringComparison.Ordinal)
            ? FakeUpstream.Status(HttpStatusCode.BadRequest, """{"error":{"message":"stream is not supported"}}""")
            : FakeUpstream.Json(Whole));

        var response = await Provider(upstream).CompleteAsync(new ProviderRequest("m", "s", "u"), default);

        Assert.Equal("whole", response.Text);
    }

    [Fact]
    public async Task An_authorization_error_is_not_hidden_by_the_fallback()
    {
        var upstream = new FakeUpstream((_, _) => FakeUpstream.Status(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}"""));

        await Assert.ThrowsAnyAsync<Exception>(() => Provider(upstream).CompleteAsync(new ProviderRequest("m", "s", "u"), default));

        Assert.Single(upstream.Requests); // no second, non-streaming attempt
    }

    [Fact]
    public async Task A_stalled_connection_is_ended_and_asked_again_once()
    {
        var calls = 0;
        var handler = new DelegatingStall(() => Interlocked.Increment(ref calls) == 1
            ? null
            : FakeUpstream.Sse(Chunk("recovered") + Chunk("", Usage) + "data: [DONE]\n\n"));
        var seen = new List<ProviderProgress>();

        var response = await Provider(handler, idle: TimeSpan.FromMilliseconds(400)).CompleteAsync(
            new ProviderRequest("m", "s", "u") { OnProgress = seen.Add }, default);

        Assert.Equal("recovered", response.Text);
        Assert.Equal(2, calls);
        Assert.Contains(seen, p => p.Retrying);
    }

    [Fact]
    public async Task Two_stalls_in_a_row_fail_with_a_clear_message()
    {
        var handler = new DelegatingStall(() => null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(handler, idle: TimeSpan.FromMilliseconds(300)).CompleteAsync(new ProviderRequest("m", "s", "u"), default));

        Assert.Contains("stopped sending data", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_ends_a_stalled_call_at_once_and_is_not_mistaken_for_a_stall()
    {
        var handler = new DelegatingStall(() => null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Provider(handler, idle: TimeSpan.FromMinutes(5)).CompleteAsync(new ProviderRequest("m", "s", "u"), cts.Token));
    }

    /// <summary>Answers with <paramref name="respond"/>'s response, or never answers when it returns null.</summary>
    private sealed class DelegatingStall(Func<HttpResponseMessage?> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _ = await request.Content!.ReadAsStringAsync(ct);
            if (respond() is { } response)
            {
                return response;
            }

            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }
}
