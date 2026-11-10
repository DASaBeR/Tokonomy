using Tokonomy.Core.Config;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Providers;

namespace Tokonomy.Runtime.Tests;

public class CodexAndCustomTests
{
    private const string Events = """
        {"type":"thread.started","thread_id":"t1"}
        {"type":"turn.started"}
        {"type":"item.completed","item":{"id":"i0","type":"reasoning","text":"thinking"}}
        {"type":"item.completed","item":{"id":"i1","type":"command_execution","command":"ls","exit_code":0}}
        {"type":"item.completed","item":{"id":"i2","type":"agent_message","text":"{\"units\":[]}"}}
        {"type":"turn.completed","usage":{"input_tokens":1200,"cached_input_tokens":1000,"output_tokens":30}}
        """;

    [Fact]
    public void Codex_events_give_the_message_usage_and_command_count()
    {
        var run = CodexRun.Parse(Events);
        Assert.Equal("{\"units\":[]}", run.Message);
        Assert.Equal((1200L, 1000L, 30L), (run.InputTokens, run.CachedTokens, run.OutputTokens));
        Assert.Equal(1, run.Commands);
        Assert.Null(run.Error);
        var usage = Assert.Single(run.ToUsage("gpt-5"));
        Assert.Equal(("gpt-5", 1200L, 30L, 1000L), (usage.Model, usage.InputTokens, usage.OutputTokens, usage.CachedTokens));
    }

    [Fact]
    public void Noise_and_unknown_events_are_ignored_and_no_usage_means_an_empty_list()
    {
        var run = CodexRun.Parse("WARN something\nnot json {\n{\"type\":\"future.event\"}\n{broken");
        Assert.False(run.HasUsage);
        Assert.Empty(run.ToUsage("m"));
    }

    [Fact]
    public void Provider_prefers_the_answer_file_and_marks_cost_as_estimated()
    {
        var response = CodexCliProvider.Parse(0, Events, "", "  final answer  ", "gpt-5");
        Assert.Equal("final answer", response.Text);
        Assert.True(response.Estimated);
        Assert.Equal(1200, response.InputTokens);
    }

    [Fact]
    public void Provider_falls_back_to_the_last_message_in_the_stream() =>
        Assert.Equal("{\"units\":[]}", CodexCliProvider.Parse(0, Events, "", "", "gpt-5").Text);

    [Theory]
    [InlineData(1, "{\"type\":\"turn.completed\",\"usage\":{}}", "ok")]
    [InlineData(0, "{\"type\":\"turn.failed\",\"error\":{\"message\":\"model not supported\"}}", "ok")]
    [InlineData(0, "{\"type\":\"error\",\"message\":\"401 Unauthorized\"}", "ok")]
    [InlineData(0, "", "")]
    public void Provider_fails_on_exit_code_error_events_or_an_empty_answer(int exit, string stdout, string answer) =>
        Assert.Throws<InvalidOperationException>(() => CodexCliProvider.Parse(exit, stdout, "stderr", answer, "gpt-5"));

    [Fact]
    public void Codex_scaffolds_valid_config_as_architect_and_worker()
    {
        var root = Path.Combine(Path.GetTempPath(), "tk-codex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Scaffolder.Init(root, force: false, new SetupChoices { Strong = Connection.Codex, Cheap = Connection.Codex, ArchitectModel = "gpt-5-codex", WorkerModel = "gpt-5-mini" });
            var config = ConfigLoader.Load(Path.Combine(root, ".tokonomy"));
            Assert.DoesNotContain(config.Issues, i => i.Severity == Severity.Error);
            Assert.Equal(ArchitectProviderConfig.CodexCliKind, config.Policy.ArchitectProvider.Kind);
            Assert.Equal("codex", config.Policy.ArchitectProvider.CommandOrDefault);
            Assert.Equal("codex", config.Policy.Executors.Default);
            Assert.Equal("codex-json", config.Executors.Executors["codex"].UsageFormat);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_custom_provider_scaffolds_as_the_workers_upstream_and_the_architect_endpoint()
    {
        var custom = Connection.For(Sources.Custom("AvalAI", "https://api.avalai.ir/v1/", "AVALAI_API_KEY"));
        var root = Path.Combine(Path.GetTempPath(), "tk-custom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Scaffolder.Init(root, force: false, new SetupChoices
            {
                Strong = custom,
                Cheap = custom,
                ArchitectModel = "gpt-4o",
                WorkerModel = "gpt-4o-mini",
                Prices = new Dictionary<string, (decimal, decimal)> { ["gpt-4o-mini"] = (0.15m, 0.6m) },
            });
            var config = ConfigLoader.Load(Path.Combine(root, ".tokonomy"));
            Assert.DoesNotContain(config.Issues, i => i.Severity == Severity.Error);
            Assert.Equal("https://api.avalai.ir/v1", config.Policy.Provider.BaseUrl);
            Assert.Equal("AVALAI_API_KEY", config.Policy.Provider.ApiKeyEnv);
            Assert.Equal("https://api.avalai.ir/v1", config.Policy.ArchitectProvider.BaseUrl);
            Assert.Contains("gpt-4o-mini", File.ReadAllText(Path.Combine(root, ".tokonomy", "prices.yaml")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_custom_provider_is_remembered_by_address_and_key_variable_name_never_the_key()
    {
        var path = Path.Combine(Path.GetTempPath(), "tk-custom-defaults-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var custom = Connection.For(Sources.Custom("AvalAI", "https://api.avalai.ir/v1", "AVALAI_API_KEY"));
            UserDefaults.Save(new SetupChoices { Strong = custom, Cheap = Connection.Codex, ArchitectModel = "a", WorkerModel = "w" }, path);

            var loaded = UserDefaults.Load(path)!;
            Assert.Equal("custom", loaded.Strong);
            Assert.Equal("codex", loaded.Cheap);
            Assert.True(UserDefaults.TryConnection(loaded.Strong, loaded.StrongCustom, out var restored));
            Assert.Equal("https://api.avalai.ir/v1", restored.Api!.BaseUrl);
            Assert.Equal("AVALAI_API_KEY", restored.Api.KeyVariable);
            Assert.False(UserDefaults.TryConnection("custom", null, out _));
            Assert.DoesNotContain("sk-", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Proxy_checks_include_the_custom_provider_a_user_set_up()
    {
        var defaults = new UserDefaults { Strong = "custom", Cheap = "codex", StrongCustom = new CustomApi("AvalAI", "https://api.avalai.ir/v1", "AVALAI_API_KEY") };
        var targets = ProxyDiagnostics.UsedTargets(defaults);
        Assert.Contains(targets, t => t.Url == "https://api.avalai.ir/v1");
        Assert.Contains(targets, t => t.Id == "codex");
    }
}
