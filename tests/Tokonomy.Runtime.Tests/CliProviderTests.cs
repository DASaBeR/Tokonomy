using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Config;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Process;
using Tokonomy.Runtime.Providers;

namespace Tokonomy.Runtime.Tests;

public sealed class AgyUsageParserTests
{
    private const string Sample =
        """{"conversation_id":"c1","status":"SUCCESS","response":"ok\n","duration_seconds":2.7,"num_turns":1,"usage":{"input_tokens":7046,"output_tokens":10,"thinking_tokens":5,"cache_read_tokens":100,"total_tokens":7061}}""";

    [Fact]
    public void Reads_tokens_and_takes_the_model_from_the_run()
    {
        var usage = Assert.Single(AgyUsageParser.Parse(Sample, "gemini-3.8-flash-low"));
        Assert.Equal("gemini-3.8-flash-low", usage.Model);
        Assert.Equal(7046, usage.InputTokens);
        Assert.Equal(15, usage.OutputTokens);
        Assert.Equal(100, usage.CachedTokens);
        Assert.Null(usage.Usd);
    }

    [Fact]
    public void Falls_back_to_a_generic_model_name() =>
        Assert.Equal("agy", Assert.Single(AgyUsageParser.Parse(Sample, null)).Model);

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("{\"status\":\"SUCCESS\"}")]
    public void Returns_empty_for_unrecognised_output(string stdout) =>
        Assert.Empty(AgyUsageParser.Parse(stdout, "m"));
}

public sealed class ClaudeCliProviderTests
{
    private const string Result =
        """{"type":"result","is_error":false,"result":"{\"units\":[]}","total_cost_usd":0.05,"modelUsage":{"claude-opus-5-5":{"inputTokens":100,"outputTokens":40,"cacheReadInputTokens":900,"cacheCreationInputTokens":50,"costUSD":0.05}}}""";

    private sealed class FakeRunner(ProcessOutcome outcome) : IProcessRunner
    {
        public ProcessSpec? Spec { get; private set; }
        public string? SystemFileText { get; private set; }

        public Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Spec = spec;
            var i = spec.Args.ToList().IndexOf("--system-prompt-file");
            SystemFileText = File.ReadAllText(spec.Args[i + 1]);
            return Task.FromResult(outcome);
        }
    }

    private static ProcessOutcome Ok(string stdout, int exit = 0) =>
        new() { ExitCode = exit, TimedOut = false, Duration = TimeSpan.Zero, Stdout = stdout, Stderr = "" };

    [Fact]
    public async Task Sends_the_prompt_on_stdin_with_no_tools_and_returns_an_estimated_response()
    {
        var runner = new FakeRunner(Ok(Result));
        var response = await new ClaudeCliProvider(runner, "claude")
            .CompleteAsync(new ProviderRequest("opus", "SYSTEM TEXT", "USER\nTEXT with \"quotes\""), default);

        Assert.Equal("{\"units\":[]}", response.Text);
        Assert.Equal("claude-opus-5-5", response.Model);
        Assert.Equal(150, response.InputTokens);
        Assert.Equal(40, response.OutputTokens);
        Assert.Equal(900, response.CachedTokens);
        Assert.Equal(0.05m, response.Usd);
        Assert.True(response.Estimated);

        var spec = runner.Spec!;
        Assert.Equal("USER\nTEXT with \"quotes\"", spec.StdinText);
        Assert.Equal("SYSTEM TEXT", runner.SystemFileText);
        Assert.Equal("claude", spec.Args[0]);
        Assert.Contains("-p", spec.Args);
        Assert.Contains("opus", spec.Args);
        var tools = spec.Args.ToList().IndexOf("--tools");
        Assert.Equal("", spec.Args[tools + 1]);
        Assert.Contains("--strict-mcp-config", spec.Args);
        var sources = spec.Args.ToList().IndexOf("--setting-sources");
        Assert.Equal("", spec.Args[sources + 1]);
        Assert.DoesNotContain("USER", string.Join(' ', spec.Args));
    }

    [Fact]
    public async Task Removes_anthropic_credentials_from_the_child_environment()
    {
        var runner = new FakeRunner(Ok(Result));
        await new ClaudeCliProvider(runner).CompleteAsync(new ProviderRequest("opus", "s", "u"), default);

        var env = runner.Spec!.Environment!;
        Assert.True(runner.Spec.ReplaceEnvironment);
        Assert.DoesNotContain(env.Keys, k => k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cleans_up_its_temp_directory()
    {
        var runner = new FakeRunner(Ok(Result));
        await new ClaudeCliProvider(runner).CompleteAsync(new ProviderRequest("opus", "s", "u"), default);
        Assert.False(Directory.Exists(runner.Spec!.WorkingDirectory));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("not json", 0)]
    [InlineData("""{"is_error":true,"result":"rate limited"}""", 0)]
    [InlineData("""{"is_error":false,"result":"text"}""", 1)]
    public async Task Fails_clearly_on_bad_output(string stdout, int exit) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ClaudeCliProvider(new FakeRunner(Ok(stdout, exit))).CompleteAsync(new ProviderRequest("opus", "s", "u"), default));

    [Fact]
    public async Task Missing_usage_still_returns_the_text_as_an_estimate()
    {
        var response = await new ClaudeCliProvider(new FakeRunner(Ok("""{"is_error":false,"result":"hi"}""")))
            .CompleteAsync(new ProviderRequest("opus", "s", "u"), default);
        Assert.Equal("hi", response.Text);
        Assert.Null(response.Usd);
        Assert.True(response.Estimated);
    }

    [Fact]
    public void Config_rejects_an_unknown_architect_provider_kind()
    {
        var policy = new Policy { ArchitectProvider = { Kind = "carrier-pigeon" } };
        Assert.Contains(ConfigLoader.Validate(policy, "policy.yaml"), i => i.Severity == Severity.Error && i.Message.Contains("architect_provider"));
        Assert.DoesNotContain(ConfigLoader.Validate(new Policy { ArchitectProvider = { Kind = "claude-cli" } }, "policy.yaml"),
            i => i.Message.Contains("architect_provider"));
    }
}
