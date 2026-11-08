using System.Collections;
using Taskrail.Core.Config;
using Taskrail.Core.Model;
using Taskrail.Runtime.Executors;
using Taskrail.Runtime.Ledger;
using Taskrail.Runtime.Orchestration;
using Taskrail.Runtime.Process;

namespace Taskrail.Runtime.Tests;

/// <summary>Records the spec and snapshots the worktree while the "agent" runs.</summary>
internal sealed class RecordingRunner(Action<ProcessSpec>? during = null) : IProcessRunner
{
    public ProcessSpec? Last { get; private set; }

    public Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        Last = spec;
        during?.Invoke(spec);
        return Task.FromResult(new ProcessOutcome { ExitCode = 0, TimedOut = false, Duration = TimeSpan.Zero, Stdout = "", Stderr = "" });
    }
}

public class ConfigExecutorTests
{
    private static ExecutorDefinition Definition() => new()
    {
        Cmd = ["agent", "run", "{prompt}"],
        ModelArgs = ["--model", "taskrail/{model}"],
        BriefFile = "AGENTS.md",
        Env = { ["AGENT_TOKEN"] = "{proxy_token}", ["AGENT_URL"] = "{proxy_url}" },
        Files = { ["agent.json"] = """{"url":"{proxy_url}","key":"{env:AGENT_TOKEN}"}""" },
        Capabilities = new ExecutorCapabilities { Headless = true, ModelOverride = true },
    };

    private static ExecutorSession Session(string logs) => new()
    {
        ProxyUrl = "http://127.0.0.1:9/v1",
        ProxyRoot = "http://127.0.0.1:9",
        ProxyToken = "trk_abc",
        LogDirectory = logs,
    };

    [Fact]
    public async Task Writes_brief_and_files_during_run_and_restores_afterwards()
    {
        using var tmp = new TempDir();
        tmp.File("AGENTS.md", "# Project rules\nUse tabs.\n");
        string? briefDuring = null, configDuring = null;
        var runner = new RecordingRunner(spec =>
        {
            briefDuring = File.ReadAllText(Path.Combine(spec.WorkingDirectory, "AGENTS.md"));
            configDuring = File.ReadAllText(Path.Combine(spec.WorkingDirectory, "agent.json"));
        });
        var executor = new ConfigExecutor("agent", Definition(), Session(Path.Combine(tmp.Path, "logs")), runner);

        await executor.RunAsync(new Brief { Prompt = "Read AGENTS.md", Body = "UNIT BRIEF", Label = "U1-a1" }, tmp.Path, "cheap-model", TimeSpan.FromMinutes(1), default);

        Assert.Contains("Use tabs.", briefDuring, StringComparison.Ordinal);
        Assert.Contains("UNIT BRIEF", briefDuring, StringComparison.Ordinal);
        Assert.Equal("""{"url":"http://127.0.0.1:9/v1","key":"{env:AGENT_TOKEN}"}""", configDuring);
        Assert.Equal("# Project rules\nUse tabs.\n", File.ReadAllText(Path.Combine(tmp.Path, "AGENTS.md")));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "agent.json")));

        var spec = runner.Last!;
        Assert.Equal(["agent", "run", "Read AGENTS.md", "--model", "taskrail/cheap-model"], spec.Args);
        Assert.True(spec.ReplaceEnvironment);
        Assert.Equal("trk_abc", spec.Environment!["AGENT_TOKEN"]);
    }

    [Fact]
    public async Task Brief_file_is_removed_when_repo_had_none()
    {
        using var tmp = new TempDir();
        var executor = new ConfigExecutor("agent", Definition(), Session(Path.Combine(tmp.Path, "logs")), new RecordingRunner());

        await executor.RunAsync(new Brief { Prompt = "p", Body = "b" }, tmp.Path, null, TimeSpan.FromMinutes(1), default);

        Assert.False(File.Exists(Path.Combine(tmp.Path, "AGENTS.md")));
    }

    [Fact]
    public void Model_args_are_omitted_without_model_or_capability()
    {
        var def = Definition();
        var vars = new Dictionary<string, string> { ["prompt"] = "p", ["model"] = "m" };

        Assert.Equal(3, ConfigExecutor.BuildArgs(def, vars, null).Count);
        def.Capabilities.ModelOverride = false;
        Assert.Equal(3, ConfigExecutor.BuildArgs(def, vars, "m").Count);
    }
}

public class EnvironmentBuilderTests
{
    private static readonly Hashtable Parent = new()
    {
        ["PATH"] = "/usr/bin",
        ["HOME"] = "/home/u",
        ["OPENROUTER_API_KEY"] = "sk-or-secret",
        ["GITHUB_TOKEN"] = "ghp_x",
        ["RANDOM_APP_SETTING"] = "1",
        ["JAVA_HOME"] = "/jdk",
    };

    [Fact]
    public void Worker_gets_allowlist_plus_executor_env_only()
    {
        var env = EnvironmentBuilder.ForWorker([], new Dictionary<string, string> { ["TASKRAIL_PROXY_TOKEN"] = "trk" }, Parent);

        Assert.Equal("/usr/bin", env["PATH"]);
        Assert.Equal("/jdk", env["JAVA_HOME"]);
        Assert.Equal("trk", env["TASKRAIL_PROXY_TOKEN"]);
        Assert.False(env.ContainsKey("OPENROUTER_API_KEY"));
        Assert.False(env.ContainsKey("GITHUB_TOKEN"));
        Assert.False(env.ContainsKey("RANDOM_APP_SETTING"));
    }

    [Fact]
    public void Extra_allowed_names_cannot_smuggle_secrets()
    {
        var env = EnvironmentBuilder.ForWorker(["RANDOM_APP_SETTING", "GITHUB_TOKEN"], new Dictionary<string, string>(), Parent);

        Assert.True(env.ContainsKey("RANDOM_APP_SETTING"));
        Assert.False(env.ContainsKey("GITHUB_TOKEN"));
    }

    [Fact]
    public void Gate_inherits_everything_except_secrets()
    {
        var env = EnvironmentBuilder.ForGate(["HOME"], Parent);

        Assert.True(env.ContainsKey("RANDOM_APP_SETTING"));
        Assert.False(env.ContainsKey("OPENROUTER_API_KEY"));
        Assert.False(env.ContainsKey("GITHUB_TOKEN"));
        Assert.False(env.ContainsKey("HOME"));
    }
}

public class RedactorTests
{
    [Theory]
    [InlineData("key=sk-or-v1-0123456789abcdef0123", "key=[REDACTED]")]
    [InlineData("Authorization: Bearer abc.def", "Authorization: Bearer [REDACTED]")]
    [InlineData("token ghp_0123456789abcdefghij0123456789abcd", "token [REDACTED]")]
    [InlineData("nothing secret here", "nothing secret here")]
    public void Redacts_known_patterns(string input, string expected) => Assert.Equal(expected, Redactor.Redact(input));

    [Fact]
    public void Redacts_known_secret_values() =>
        Assert.Equal("x [REDACTED] y", Redactor.Redact("x custom-secret-value y", ["custom-secret-value"]));
}

public class BudgetGuardTests
{
    [Fact]
    public void Cancels_token_when_limit_reached()
    {
        using var guard = new BudgetGuard(1.00m, alreadySpent: 0.40m);
        guard.Add(0.50m);
        Assert.False(guard.Exhausted);

        guard.Add(0.10m);
        Assert.True(guard.Exhausted);
        Assert.True(guard.Token.IsCancellationRequested);
        Assert.Equal(1.00m, guard.Spent);
    }

    [Fact]
    public void Planning_spend_alone_can_exhaust_budget()
    {
        using var guard = new BudgetGuard(1.00m, alreadySpent: 1.50m);
        Assert.True(guard.Exhausted);
    }
}
