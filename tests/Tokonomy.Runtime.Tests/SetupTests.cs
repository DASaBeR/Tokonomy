using System.Net;
using Tokonomy.Core.Config;
using Tokonomy.Llm.Providers;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Providers;

namespace Tokonomy.Runtime.Tests;

public sealed class SetupTests
{
    private static string Model(string id, string prompt, string completion, int context = 128_000, long created = 100, bool tools = true, string output = "text")
    {
        var parameters = tools ? "\"tools\",\"temperature\"" : "\"temperature\"";
        return $$"""{"id":"{{id}}","name":"{{id}}","created":{{created}},"context_length":{{context}},"architecture":{"output_modalities":["{{output}}"]},"pricing":{"prompt":"{{prompt}}","completion":"{{completion}}"},"supported_parameters":[{{parameters}}]}""";
    }

    private static string Catalog(params string[] models) => "{\"data\":[" + string.Join(",", models) + "]}";

    [Fact]
    public void Parse_keeps_only_tool_capable_text_models_and_converts_prices_to_per_million()
    {
        var json = Catalog(
            Model("anthropic/a", "0.000003", "0.000015"),
            Model("x/no-tools", "0.000001", "0.000001", tools: false),
            Model("x/image-gen", "0.000001", "0.000001", output: "image"),
            Model("x/tiny-context", "0.000001", "0.000001", context: 8_000),
            Model("x/router", "-1", "-1"));

        var models = OpenRouterCatalog.Parse(json);

        var only = Assert.Single(models);
        Assert.Equal("anthropic/a", only.Id);
        Assert.Equal(3m, only.InputPerM);
        Assert.Equal(15m, only.OutputPerM);
        Assert.Equal("anthropic", only.Vendor);
    }

    [Fact]
    public void Curate_offers_about_twenty_models_with_at_least_two_free_and_no_vendor_crowding()
    {
        var models = new List<string>();
        foreach (var vendor in new[] { "anthropic", "openai", "google", "deepseek", "qwen", "x-ai", "moonshotai", "z-ai", "mistralai", "meta-llama" })
        {
            for (var i = 0; i < 6; i++)
            {
                models.Add(Model($"{vendor}/m{i}", "0.000002", "0.00001", created: 1000 + i));
            }
        }

        for (var i = 0; i < 5; i++)
        {
            models.Add(Model($"free/f{i}:free", "0", "0", created: 5000 + i));
        }

        var curated = OpenRouterCatalog.Curate(OpenRouterCatalog.Parse(Catalog([.. models])));

        Assert.Equal(20, curated.Count);
        Assert.True(curated.Count(m => m.IsFree) >= 2);
        Assert.Equal(curated.Count, curated.Select(m => m.Id).Distinct().Count());
        Assert.All(curated.Where(m => !m.IsFree).GroupBy(m => m.Vendor), g => Assert.True(g.Count() <= 3));
        Assert.Contains(curated, m => m.Id == "anthropic/m5");
        Assert.DoesNotContain(curated, m => m.Id == "anthropic/m0");
    }

    [Fact]
    public void Curate_with_fewer_free_models_than_wanted_returns_what_exists()
    {
        var curated = OpenRouterCatalog.Curate(OpenRouterCatalog.Parse(Catalog(
            Model("anthropic/a", "0.000003", "0.000015"), Model("f/one:free", "0", "0"))));

        Assert.Equal(2, curated.Count);
        Assert.Single(curated, m => m.IsFree);
    }

    [Fact]
    public void Role_orderings_put_strong_models_by_price_and_cheap_ones_free_first()
    {
        var curated = OpenRouterCatalog.Parse(Catalog(
            Model("a/cheap", "0.0000001", "0.0000002"), Model("a/pricey", "0.00001", "0.00005"), Model("f/free:free", "0", "0")));

        Assert.Equal("a/pricey", OpenRouterCatalog.ForStrongRole(curated)[0].Id);
        Assert.Equal("f/free:free", OpenRouterCatalog.ForStrongRole(curated)[^1].Id);
        Assert.Equal("f/free:free", OpenRouterCatalog.ForCheapRole(curated)[0].Id);
        Assert.Equal("a/cheap", OpenRouterCatalog.ForCheapRole(curated)[1].Id);
    }

    private static readonly Connection[] AllConnections =
        [Connection.Claude, Connection.Antigravity, .. Sources.ApiServices.Select(Connection.For)];

    public static IEnumerable<object[]> Pairs() =>
        from strong in AllConnections
        from cheap in AllConnections
        select new object[] { UserDefaults.Id(strong), UserDefaults.Id(cheap) };

    private static Connection Conn(string id)
    {
        Assert.True(Sources.TryParse(id, out var c));
        return c;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Every_strong_and_cheap_combination_writes_valid_config(string strongId, string cheapId)
    {
        var strong = Conn(strongId);
        var cheap = Conn(cheapId);
        var root = Path.Combine(Path.GetTempPath(), "tk-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var choices = new SetupChoices
            {
                Strong = strong,
                Cheap = cheap,
                ArchitectModel = "strong-model",
                WorkerModel = "cheap-model",
                Acceptance = Sources.DetectAcceptance(root),
                Prices = new Dictionary<string, (decimal, decimal)> { ["anthropic/claude-sonnet-5-5"] = (3m, 15m) },
            };

            Scaffolder.Init(root, force: false, choices);
            var config = ConfigLoader.Load(Path.Combine(root, ".tokonomy"));

            Assert.DoesNotContain(config.Issues, i => i.Severity == Severity.Error);
            Assert.Equal(Sources.Executor(cheap), config.Policy.Executors.Default);
            Assert.Contains(Sources.Executor(cheap), config.Executors.Executors.Keys);
            var kind = strong.Kind switch
            {
                ConnectionKind.ClaudeCode => ArchitectProviderConfig.ClaudeCliKind,
                ConnectionKind.Antigravity => ArchitectProviderConfig.AgyCliKind,
                _ => ArchitectProviderConfig.ApiKind,
            };
            Assert.Equal(kind, config.Policy.ArchitectProvider.Kind);
            Assert.Equal("strong-model", config.Policy.Models.Architect);
            Assert.Equal("cheap-model", config.Policy.Models.Worker);

            // The strong model's endpoint and key are its own; the worker's metering endpoint is its own.
            if (strong.IsApi)
            {
                var effective = config.Policy.ArchitectProvider.EffectiveProvider(config.Policy.Provider);
                Assert.Equal(strong.Api!.BaseUrl, effective.BaseUrl);
                Assert.Equal(strong.Api.KeyVariable, effective.ApiKeyEnv);
            }

            if (cheap.IsApi)
            {
                Assert.Equal(cheap.Api!.BaseUrl, config.Policy.Provider.BaseUrl);
                Assert.Equal(cheap.Api.KeyVariable, config.Policy.Provider.ApiKeyEnv);
            }

            Assert.True(PriceTable.Load(Path.Combine(root, ".tokonomy", "prices.yaml")).Has("anthropic/claude-sonnet-5-5"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Two_different_api_services_keep_separate_endpoints_and_keys()
    {
        var root = Path.Combine(Path.GetTempPath(), "tk-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Scaffolder.Init(root, false, new SetupChoices
            {
                Strong = Connection.For(Sources.Anthropic), Cheap = Connection.For(Sources.DeepSeek), ArchitectModel = "a", WorkerModel = "b",
            });
            var policy = ConfigLoader.Load(Path.Combine(root, ".tokonomy")).Policy;

            Assert.Equal("ANTHROPIC_API_KEY", policy.ArchitectProvider.EffectiveProvider(policy.Provider).ApiKeyEnv);
            Assert.Equal("DEEPSEEK_API_KEY", policy.Provider.ApiKeyEnv);
            Assert.Equal(2, policy.KeyVariables.Count);
            Assert.Contains("ANTHROPIC_API_KEY", policy.KeyVariables);
            Assert.Contains("DEEPSEEK_API_KEY", policy.KeyVariables);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("claude", "antigravity", "claude-code-sub")]
    [InlineData("antigravity", "claude", "antigravity")]
    [InlineData("claude", "claude", null)]
    [InlineData("claude", "openrouter", "claude-code-sub")]
    public void A_separate_executor_for_the_strong_model_is_written_only_when_it_differs(string strongId, string cheapId, string? expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "tk-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Scaffolder.Init(root, false, new SetupChoices { Strong = Conn(strongId), Cheap = Conn(cheapId), ArchitectModel = "a", WorkerModel = "b" });
            var config = ConfigLoader.Load(Path.Combine(root, ".tokonomy"));

            Assert.DoesNotContain(config.Issues, i => i.Severity == Severity.Error);
            Assert.Equal(expected, config.Policy.Executors.Strong);
            Assert.Equal(expected ?? config.Policy.Executors.Default, config.Policy.Executors.For(strongModelDoesTheWork: true));
            Assert.Equal(config.Policy.Executors.Default, config.Policy.Executors.For(strongModelDoesTheWork: false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_relative_config_path_is_resolved_so_artifacts_exist_for_workers_in_other_directories()
    {
        var root = Path.Combine(Path.GetTempPath(), "tk-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Scaffolder.Init(root, false, new SetupChoices { Strong = Connection.Claude, Cheap = Connection.Claude, ArchitectModel = "a", WorkerModel = "b" });
            Directory.SetCurrentDirectory(root);

            var config = ConfigLoader.Load(".tokonomy");

            Assert.True(Path.IsPathRooted(config.ConfigDirectory));
            Assert.True(Path.IsPathRooted(config.LedgerPath));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Host_unsafe_isolation_is_written_only_when_chosen()
    {
        var root = Path.Combine(Path.GetTempPath(), "tk-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Scaffolder.Init(root, false, new SetupChoices { Strong = Connection.Claude, Cheap = Connection.Claude, ArchitectModel = "a", WorkerModel = "b", GateExecution = "host-unsafe" });

            Assert.Equal("host-unsafe", ConfigLoader.Load(Path.Combine(root, ".tokonomy")).Policy.GateExecution!.Provider);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("claude-antigravity", "ClaudeCode", "Antigravity")]
    [InlineData("claude", "ClaudeCode", "ClaudeCode")]
    [InlineData("api", "Api", "Api")]
    public void Presets_are_shortcuts_for_a_strong_and_cheap_pair(string preset, string strong, string cheap)
    {
        Assert.True(Sources.TryPreset(preset, out var s, out var c));
        Assert.Equal((strong, cheap), (s.Kind.ToString(), c.Kind.ToString()));
    }

    [Theory]
    [InlineData("claude", true)]
    [InlineData("agy", true)]
    [InlineData("OpenRouter", true)]
    [InlineData("gemini", true)]
    [InlineData("deepseek", true)]
    [InlineData("gpt", false)]
    public void Sources_parse_from_flags(string text, bool ok) => Assert.Equal(ok, Sources.TryParse(text, out _));

    [Fact]
    public void Antigravity_has_no_api_key_mode_but_gemini_has_its_own_api_connection()
    {
        Assert.True(Sources.TryParse("antigravity", out var agy));
        Assert.False(agy.IsApi);
        Assert.True(Sources.TryParse("gemini", out var gemini));
        Assert.True(gemini.IsApi);
        Assert.Equal("GEMINI_API_KEY", gemini.Api!.KeyVariable);
    }

    // --- Antigravity as the strong model ---

    private const string AgyOk = """
        {"event":"init","conversation_id":"c1","init":{"model":"gemini-3.8-flash-medium","cwd":"x","tools":["view_file"],"permission_mode":"request-review"}}
        {"event":"step_update","step_update":{"conversation_id":"c1","step_index":1,"state":"DONE","step_type":"agent_response","text_delta":"\n"}}
        {"event":"result","result":{"conversation_id":"c1","status":"SUCCESS","response":"{\"units\":[]}\n","usage":{"input_tokens":6937,"output_tokens":23,"thinking_tokens":22,"cache_read_tokens":10,"total_tokens":6960}}}
        """;

    [Fact]
    public void Antigravity_planner_returns_the_answer_with_usage_as_an_estimate()
    {
        var response = AgyCliProvider.Parse(0, AgyOk, "", "gemini-3.8-flash-medium");

        Assert.Equal("{\"units\":[]}", response.Text);
        Assert.Equal("gemini-3.8-flash-medium", response.Model);
        Assert.Equal(6937, response.InputTokens);
        Assert.Null(response.Usd);
        Assert.True(response.Estimated);
    }

    [Fact]
    public void Antigravity_planner_refuses_a_different_model_an_error_or_no_result()
    {
        Assert.Contains("instead of", Assert.Throws<InvalidOperationException>(() => AgyCliProvider.Parse(0, AgyOk, "", "gemini-9-ultra")).Message, StringComparison.Ordinal);
        Assert.Contains("no result", Assert.Throws<InvalidOperationException>(() => AgyCliProvider.Parse(1, "boom", "bad", "m")).Message, StringComparison.Ordinal);
        var failed = AgyOk.Replace("\"SUCCESS\"", "\"ERROR\"", StringComparison.Ordinal);
        Assert.Contains("failed", Assert.Throws<InvalidOperationException>(() => AgyCliProvider.Parse(0, failed, "", "gemini-3.8-flash-medium")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Antigravity_planner_discards_an_answer_that_used_a_forbidden_tool()
    {
        var withTool = AgyOk + "\n" + """{"event":"step_update","step_update":{"conversation_id":"c1","step_index":3,"state":"ERROR","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"dir"}}}}""";

        Assert.Contains("not allowed", Assert.Throws<InvalidOperationException>(() => AgyCliProvider.Parse(0, withTool, "", "gemini-3.8-flash-medium")).Message, StringComparison.Ordinal);
    }

    // --- keys and model lists for any API ---

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    public async Task A_key_is_checked_against_its_own_service_and_only_a_rejection_counts_as_bad(HttpStatusCode status, bool ok)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(status));
        using var http = new HttpClient(handler);

        var check = await ApiModels.CheckKeyAsync(http, "https://api.example.com/v1/", "secret-key", openRouter: false, default);

        Assert.Equal(ok, check.Ok);
        Assert.Equal("https://api.example.com/v1/models", handler.Requests.Single().RequestUri!.ToString());
        Assert.Equal("Bearer secret-key", handler.Requests.Single().Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task A_network_failure_is_not_reported_as_a_bad_key()
    {
        using var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline")));

        var check = await ApiModels.CheckKeyAsync(http, "https://api.example.com/v1", "k", openRouter: false, default);

        Assert.True(check.Ok);
        Assert.Contains("offline", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRouter_keys_are_checked_on_its_key_endpoint()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);

        await ApiModels.CheckKeyAsync(http, Sources.OpenRouter.BaseUrl, "k", openRouter: true, default);

        Assert.Equal("https://openrouter.ai/api/v1/auth/key", handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public void Model_lists_drop_non_chat_models_strip_google_prefixes_and_put_the_newest_first()
    {
        var json = """
            {"data":[{"id":"models/gemini-old","created":1},{"id":"text-embedding-3","created":9},{"id":"gemini-new","created":5},
                     {"id":"whisper-1","created":3},{"id":"gpt-image-1","created":4}]}
            """;

        Assert.Equal(["gemini-new", "gemini-old"], ApiModels.ParseList(json));
    }

    [Fact]
    public async Task Anthropic_models_come_from_the_fixed_list_because_its_compatible_layer_does_not_list()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);

        var models = await ApiModels.ForServiceAsync(http, Sources.Anthropic, "k", default);

        Assert.Contains("claude-opus-5-5", models);
        Assert.Empty(handler.Requests);
    }

    // --- machine scan, defaults, config details ---

    [Fact]
    public void Agy_models_output_is_parsed_one_id_per_line()
    {
        Assert.Equal(["gemini-3.8-flash-medium", "gemini-3.8-pro"], MachineScan.ParseModels("gemini-3.8-flash-medium\tdefault\n\ngemini-3.8-pro\tpro plan\nnot a model line\n"));
    }

    [Fact]
    public void A_tool_is_ready_only_when_installed_and_not_known_to_be_signed_out()
    {
        Assert.True(new ToolState("t", true, true, "", "").Ready);
        Assert.True(new ToolState("t", true, null, "", "").Ready);
        Assert.False(new ToolState("t", true, false, "", "").Ready);
        Assert.False(new ToolState("t", false, null, "", "").Ready);
    }

    [Fact]
    public void Defaults_round_trip_without_any_secret()
    {
        var path = Path.Combine(Path.GetTempPath(), "tk-defaults-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            UserDefaults.Save(new SetupChoices { Strong = Connection.For(Sources.Gemini), Cheap = Connection.Antigravity, ArchitectModel = "g", WorkerModel = "w" }, path);

            var loaded = UserDefaults.Load(path)!;

            Assert.Equal(("gemini", "antigravity", "g", "w"), (loaded.Strong, loaded.Cheap, loaded.ArchitectModel, loaded.WorkerModel));
            Assert.DoesNotContain("KEY", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validation_accepts_agy_cli_and_rejects_a_key_pasted_where_a_variable_name_belongs()
    {
        var good = new Policy { ArchitectProvider = { Kind = ArchitectProviderConfig.AgyCliKind } };
        var bad = new Policy { ArchitectProvider = { Kind = "api", ApiKeyEnv = "sk-or-v1-0123456789abcdef0123456789abcdef" } };

        Assert.DoesNotContain(ConfigLoader.Validate(good, "p.yaml"), i => i.Message.Contains("architect_provider", StringComparison.Ordinal) && i.Severity == Severity.Error);
        Assert.Contains(ConfigLoader.Validate(bad, "p.yaml"), i => i.Message.Contains("architect_provider.api_key_env", StringComparison.Ordinal));
    }
}
