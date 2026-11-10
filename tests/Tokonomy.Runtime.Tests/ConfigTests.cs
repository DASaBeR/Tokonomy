using Tokonomy.Core.Config;
using Tokonomy.Runtime.Config;

namespace Tokonomy.Runtime.Tests;

public class StackDetectorTests
{
    [Fact]
    public void Dotnet_gates_are_argument_arrays_starting_with_dotnet()
    {
        using var tmp = new TempDir();
        tmp.File("App.sln", "");

        var stack = Assert.Single(StackDetector.Detect(tmp.Path));

        Assert.Equal("dotnet", stack.Name);
        Assert.All(stack.Gates, g => Assert.Equal("dotnet", g.Cmd[0]));
        Assert.Equal(["dotnet", "build", "--nologo"], stack.Gates[0].Cmd);
        Assert.Null(stack.Gates[0].Report);
        Assert.EndsWith("test-results.trx", stack.Gates.Single(g => g.Name == "test").Report, StringComparison.Ordinal);
    }

    [Fact]
    public void Node_gates_depend_on_package_json_contents()
    {
        using var tmp = new TempDir();
        tmp.File("package.json", """
            { "scripts": { "test": "vitest" }, "devDependencies": { "typescript": "5.0.0" } }
            """);

        var stack = Assert.Single(StackDetector.Detect(tmp.Path));

        Assert.Contains(stack.Gates, g => g.Name == "test");
        Assert.Contains(stack.Gates, g => g.Name == "types");
        Assert.DoesNotContain(stack.Gates, g => g.Name == "lint");
    }

    [Fact]
    public void Npm_placeholder_test_script_is_ignored()
    {
        using var tmp = new TempDir();
        tmp.File("package.json", """{ "scripts": { "test": "echo \"Error: no test specified\" && exit 1" } }""");

        Assert.Empty(Assert.Single(StackDetector.Detect(tmp.Path)).Gates);
    }

    [Fact]
    public void Python_and_go_are_detected_together()
    {
        using var tmp = new TempDir();
        tmp.File("pyproject.toml", "");
        tmp.File("go.mod", "");

        var names = StackDetector.Detect(tmp.Path).Select(s => s.Name).ToList();

        Assert.Equal(["python", "go"], names);
    }

    [Fact]
    public void Empty_directory_detects_nothing()
    {
        using var tmp = new TempDir();
        Assert.Empty(StackDetector.Detect(tmp.Path));
    }
}

public class ConfigLoaderTests
{
    [Fact]
    public void Scaffolded_config_loads_without_errors()
    {
        using var tmp = new TempDir();
        tmp.File("pyproject.toml", "");
        var result = Scaffolder.Init(tmp.Path, force: false);

        var config = ConfigLoader.Load(result.ConfigDirectory);

        Assert.False(config.HasErrors, string.Join("\n", config.Issues));
        Assert.Equal(["ruff", "check", "."], config.Policy.Gates[0].Cmd);
        Assert.Equal(GateConfig.LockedRegionsType, config.Policy.Gates[^1].Type);
        Assert.Contains("opencode", config.Executors.Executors.Keys);
        Assert.True(config.Executors.Executors["opencode"].Capabilities.Headless);
        Assert.Equal(Path.GetFullPath(tmp.Path).TrimEnd(Path.DirectorySeparatorChar), config.RepoRoot);
    }

    [Fact]
    public void Opencode_adapter_uses_the_v1_config_shape_it_was_verified_with()
    {
        // OpenCode 1.19.5 (npm `opencode-ai`) rejects the V2 shape (`providers`/`permissions`) outright.
        using var tmp = new TempDir();
        var config = ConfigLoader.Load(Scaffolder.Init(tmp.Path, force: false).ConfigDirectory);

        var opencode = config.Executors.Executors["opencode"];
        var json = opencode.Files["opencode.json"];

        Assert.Equal("1.19.5", opencode.TestedVersion);
        Assert.Contains("\"provider\":", json, StringComparison.Ordinal);
        Assert.Contains("\"permission\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"providers\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"permissions\"", json, StringComparison.Ordinal);
        Assert.Contains("{proxy_url}", json, StringComparison.Ordinal);
        Assert.Equal(["--model", "tokonomy/{model}"], opencode.ModelArgs);
    }

    [Fact]
    public void Init_keeps_existing_files_unless_forced()
    {
        using var tmp = new TempDir();
        Scaffolder.Init(tmp.Path, force: false);
        File.WriteAllText(Path.Combine(tmp.Path, ".tokonomy", "policy.yaml"), "custom: true");

        var second = Scaffolder.Init(tmp.Path, force: false);

        Assert.Contains("policy.yaml", second.Skipped);
        Assert.Equal("custom: true", File.ReadAllText(Path.Combine(tmp.Path, ".tokonomy", "policy.yaml")));
    }

    [Fact]
    public void Unknown_key_reports_line_number()
    {
        using var tmp = new TempDir();
        var dir = Scaffolder.Init(tmp.Path, force: false).ConfigDirectory;
        File.WriteAllText(Path.Combine(dir, "policy.yaml"), "models:\n  architect: x\n  wroker: y\n");

        var config = ConfigLoader.Load(dir);

        var issue = Assert.Single(config.Issues, i => i.Severity == Severity.Error);
        Assert.Equal(3, issue.Line);
    }

    [Theory]
    [InlineData("provider:\n  api_key_env: sk-or-v1-abcdef0123456789abcdef0123456789\n", "looks like an API key")]
    [InlineData("gates:\n  - { cmd: [] }\n", "non-empty cmd")]
    [InlineData("gates:\n  - { type: magic }\n", "unknown type")]
    [InlineData("budget:\n  max_usd_per_task: 0\n", "max_usd_per_task")]
    [InlineData("executors:\n  default: nope\n", "not defined")]
    [InlineData("provider:\n  base_url: ftp://x\n", "http(s)")]
    public void Invalid_policy_is_rejected(string yaml, string expectedMessage)
    {
        using var tmp = new TempDir();
        var dir = Scaffolder.Init(tmp.Path, force: false).ConfigDirectory;
        File.WriteAllText(Path.Combine(dir, "policy.yaml"), yaml);

        var config = ConfigLoader.Load(dir);

        Assert.Contains(config.Issues, i => i.Severity == Severity.Error && i.Message.Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void Shell_string_gate_produces_warning()
    {
        var issues = ConfigLoader.Validate(new Policy { Gates = [new GateConfig { Cmd = ["npm run test"] }] }, "policy.yaml");

        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("shell string", StringComparison.Ordinal));
    }

    [Fact]
    public void Literal_secret_in_executor_env_is_rejected()
    {
        var file = new ExecutorsFile
        {
            Executors = { ["x"] = new ExecutorDefinition { Cmd = ["x"], Env = { ["KEY"] = "sk-ant-api03-abc" } } },
        };

        Assert.Contains(ConfigLoader.Validate(file, "executors.yaml"), i => i.Severity == Severity.Error);
    }

    [Fact]
    public void Proxy_placeholders_in_executor_env_are_allowed()
    {
        var file = new ExecutorsFile
        {
            Executors = { ["x"] = new ExecutorDefinition { Cmd = ["x"], Env = { ["BASE"] = "{proxy_url}", ["TOKEN"] = "{proxy_token}" } } },
        };

        Assert.DoesNotContain(ConfigLoader.Validate(file, "executors.yaml"), i => i.Severity == Severity.Error);
    }

    [Fact]
    public void Missing_files_are_errors()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, ".tokonomy"));

        var config = ConfigLoader.Load(Path.Combine(tmp.Path, ".tokonomy"));

        Assert.Equal(2, config.Issues.Count(i => i.Severity == Severity.Error));
    }

    [Fact]
    public void Finds_config_directory_in_parent()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, ".tokonomy"));
        var nested = Directory.CreateDirectory(Path.Combine(tmp.Path, "a", "b")).FullName;

        Assert.Equal(Path.Combine(tmp.Path, ".tokonomy"), ConfigLoader.FindConfigDirectory(nested));
    }
}

public class GateExecutionValidationTests
{
    [Fact]
    public void Scaffolded_policy_isolates_gates_in_windows_sandbox_by_default()
    {
        using var tmp = new TempDir();
        tmp.File("pyproject.toml", "[project]\nname='x'\n");

        var config = ConfigLoader.Load(Scaffolder.Init(tmp.Path, force: false).ConfigDirectory);

        Assert.Equal(GateExecutionConfig.WindowsSandboxProvider, config.Policy.GateExecution?.Provider);
        Assert.Equal(GateExecutionConfig.NetworkNone, config.Policy.GateExecution!.NetworkPolicy);
    }

    [Fact]
    public void A_policy_with_gates_but_no_gate_execution_is_warned_about()
    {
        var issues = ConfigLoader.Validate(new Policy { Gates = [new GateConfig { Cmd = ["pytest"] }] }, "policy.yaml").ToList();

        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("gate_execution is not set", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("docker", "not supported")]
    [InlineData("", "is required")]
    public void An_unknown_or_empty_provider_is_an_error(string provider, string message)
    {
        var issues = ConfigLoader.Validate(new Policy { GateExecution = new() { Provider = provider } }, "policy.yaml");

        Assert.Contains(issues, i => i.Severity == Severity.Error && i.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void Host_unsafe_and_full_network_are_flagged()
    {
        var issues = ConfigLoader.Validate(new Policy { GateExecution = new() { Provider = "host-unsafe", NetworkPolicy = "full" } }, "policy.yaml").ToList();

        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("host-unsafe", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("network_policy is full", StringComparison.Ordinal));
    }

    [Fact]
    public void Bad_values_are_errors()
    {
        var policy = new Policy
        {
            GateExecution = new()
            {
                Provider = "windows-sandbox",
                NetworkPolicy = "some",
                CleanupPolicy = "never",
                ResourceLimits = { MemoryMb = 0 },
                Toolchains = ["relative/dotnet"],
            },
        };

        var errors = ConfigLoader.Validate(policy, "policy.yaml").Where(i => i.Severity == Severity.Error).Select(i => i.Message).ToList();

        Assert.Contains(errors, m => m.Contains("network_policy", StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("cleanup_policy", StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("memory_mb", StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("absolute path", StringComparison.Ordinal));
    }

    [Fact]
    public void The_yaml_keys_load()
    {
        using var tmp = new TempDir();
        var dir = Scaffolder.Init(tmp.Path, force: false).ConfigDirectory;
        var path = Path.Combine(dir, "policy.yaml");
        var yaml = File.ReadAllText(path);
        var start = yaml.IndexOf("gate_execution:", StringComparison.Ordinal);
        File.WriteAllText(path, yaml[..start] + """
            gate_execution:
              provider: windows-sandbox
              network_policy: none
              prepare_network: none
              startup_timeout_seconds: 240
              resource_limits: { memory_mb: 8192 }
              artifact_limits: { max_snapshot_mb: 100, max_dependencies_mb: 900, max_log_kb: 512, max_report_mb: 4 }
              cleanup_policy: keep-on-failure
              toolchains: ["C:/Program Files/dotnet"]
            """);

        var g = ConfigLoader.Load(dir).Policy.GateExecution!;

        Assert.Equal(240, g.StartupTimeoutSeconds);
        Assert.Equal(8192, g.ResourceLimits.MemoryMb);
        Assert.Equal(900, g.ArtifactLimits.MaxDependenciesMb);
        Assert.Equal("keep-on-failure", g.CleanupPolicy);
        Assert.Equal("none", g.PrepareNetwork);
        Assert.Equal(["C:/Program Files/dotnet"], g.Toolchains);
    }
}
