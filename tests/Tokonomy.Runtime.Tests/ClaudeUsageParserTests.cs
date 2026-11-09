using Tokonomy.Runtime.Executors;

namespace Tokonomy.Runtime.Tests;

public sealed class ClaudeUsageParserTests
{
    private const string Sample =
        """{"type":"result","total_cost_usd":0.0137,"usage":{"input_tokens":2,"cache_creation_input_tokens":100,"cache_read_input_tokens":300,"output_tokens":32},"modelUsage":{"claude-haiku-5-5":{"inputTokens":1176,"outputTokens":52,"cacheReadInputTokens":29992,"cacheCreationInputTokens":13682,"costUSD":0.0137}}}""";

    [Fact]
    public void Reads_per_model_usage()
    {
        var usage = Assert.Single(ClaudeUsageParser.Parse(Sample));
        Assert.Equal("claude-haiku-5-5", usage.Model);
        Assert.Equal(1176 + 13682, usage.InputTokens);
        Assert.Equal(52, usage.OutputTokens);
        Assert.Equal(29992, usage.CachedTokens);
        Assert.Equal(0.0137m, usage.Usd);
    }

    [Fact]
    public void Falls_back_to_top_level_usage()
    {
        var json = """{"total_cost_usd":0.5,"usage":{"input_tokens":10,"cache_creation_input_tokens":5,"cache_read_input_tokens":7,"output_tokens":3}}""";
        var usage = Assert.Single(ClaudeUsageParser.Parse(json));
        Assert.Equal(15, usage.InputTokens);
        Assert.Equal(7, usage.CachedTokens);
        Assert.Equal(3, usage.OutputTokens);
        Assert.Equal(0.5m, usage.Usd);
    }

    [Fact]
    public void Tolerates_leading_noise_on_an_earlier_line()
    {
        var usage = Assert.Single(ClaudeUsageParser.Parse("warning: something\n" + Sample));
        Assert.Equal("claude-haiku-5-5", usage.Model);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"result\":\"ok\"}")]
    public void Returns_empty_for_unrecognised_output(string stdout) =>
        Assert.Empty(ClaudeUsageParser.Parse(stdout));
}
