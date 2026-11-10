using System.Collections;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Tests;

/// <summary>What a worker, and the gates that run its code, can see of the user's credentials.</summary>
public class CredentialIsolationTests
{
    private static Hashtable Parent() => new()
    {
        ["PATH"] = "C:/bin",
        ["USERPROFILE"] = "C:/Users/u",
        ["HOME"] = "/home/u",
        ["APPDATA"] = "C:/Users/u/AppData/Roaming",
        ["LOCALAPPDATA"] = "C:/Users/u/AppData/Local",
        ["TEMP"] = "C:/Temp",
        // Agent logins and provider keys.
        ["ANTHROPIC_API_KEY"] = "sk-ant-api03-xxxxxxxxxxxxxxxxxxxxxxxx",
        ["ANTHROPIC_AUTH_TOKEN"] = "t",
        ["CLAUDE_CODE_OAUTH_TOKEN"] = "sk-ant-oat01-xxxxxxxxxxxxxxxxxxxxxxxx",
        ["GEMINI_API_KEY"] = "AIzaXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
        ["GOOGLE_API_KEY"] = "AIzaYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYYY",
        ["GOOGLE_APPLICATION_CREDENTIALS"] = "C:/Users/u/key.json",
        ["OPENROUTER_API_KEY"] = "sk-or-v1-xxxxxxxxxxxxxxxx",
        ["AWS_SECRET_ACCESS_KEY"] = "x",
        ["GITHUB_TOKEN"] = "ghp_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
        ["GH_TOKEN"] = "x",
        ["NPM_TOKEN"] = "x",
        // Things that grant access without looking like a key.
        ["SSH_AUTH_SOCK"] = "/tmp/ssh-agent.sock",
        ["GPG_AGENT_INFO"] = "/run/gpg",
        ["DATABASE_URL"] = "postgres://app:hunter2@db:5432/prod",
        ["REDIS_URL"] = "redis://default:s3cr3t@cache:6379",
        ["SQLSERVER_CONN"] = "Server=db;User Id=sa;Password=hunter2;",
        ["SESSION_COOKIE"] = "abc",
        // Proxies: plain is needed, with credentials is not.
        ["HTTPS_PROXY"] = "http://alice:pa55@proxy.corp:8080",
        ["HTTP_PROXY"] = "http://proxy.corp:8080",
    };

    private static readonly string[] Leaks =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "GEMINI_API_KEY", "GOOGLE_API_KEY",
        "GOOGLE_APPLICATION_CREDENTIALS", "OPENROUTER_API_KEY", "AWS_SECRET_ACCESS_KEY", "GITHUB_TOKEN", "GH_TOKEN",
        "NPM_TOKEN", "SSH_AUTH_SOCK", "GPG_AGENT_INFO", "DATABASE_URL", "REDIS_URL", "SQLSERVER_CONN", "SESSION_COOKIE",
        "HTTPS_PROXY",
    ];

    [Fact]
    public void The_worker_sees_no_credentials_but_keeps_what_its_cli_login_needs()
    {
        var env = EnvironmentBuilder.ForWorker([], new Dictionary<string, string>(), Parent());

        foreach (var name in Leaks)
        {
            Assert.False(env.ContainsKey(name), $"{name} reached the worker");
        }

        // agy finds its own login through the profile folders; removing these would break subscription auth.
        foreach (var name in new[] { "PATH", "USERPROFILE", "HOME", "APPDATA", "LOCALAPPDATA", "TEMP", "HTTP_PROXY" })
        {
            Assert.True(env.ContainsKey(name), $"{name} should reach the worker");
        }
    }

    [Fact]
    public void Gates_which_run_worker_written_code_see_no_credentials_either()
    {
        var env = EnvironmentBuilder.ForGate([], Parent());

        foreach (var name in Leaks)
        {
            Assert.False(env.ContainsKey(name), $"{name} reached the gate");
        }

        Assert.Equal("C:/bin", env["PATH"]);
    }

    [Fact]
    public void Allowlisting_a_name_does_not_let_a_credential_value_through()
    {
        var env = EnvironmentBuilder.ForWorker(["DATABASE_URL", "MY_SERVICE"], new Dictionary<string, string>(),
            new Hashtable { ["MY_SERVICE"] = "https://bob:pw@svc.local/api", ["DATABASE_URL"] = "postgres://a:b@h/db" });

        Assert.Empty(env);
    }

    [Theory]
    [InlineData("http://proxy:8080", false)]
    [InlineData("https://example.com/path?x=1", false)]
    [InlineData("C:\\Users\\u", false)]
    [InlineData("http://user:pass@proxy:8080", true)]
    [InlineData("mongodb+srv://u:p@cluster/db", true)]
    [InlineData("Server=x;Password=y", true)]
    [InlineData("server=x; pwd=y", true)]
    public void Credential_values_are_recognised(string value, bool secret) =>
        Assert.Equal(secret, EnvironmentBuilder.LooksSecretValue(value));

    [Fact]
    public void The_agy_worker_is_denied_both_agents_logins_and_common_credential_folders()
    {
        var file = AgySettings.Build(Path.Combine(Path.GetTempPath(), "wt-cred"), new ExecutorPermissions(), [], homeDirectory: "/home/u");

        foreach (var name in new[] { ".gemini", ".claude", ".claude.json", ".ssh", ".aws", ".config", ".git-credentials", ".netrc" })
        {
            Assert.Contains($"read_file({Path.Combine("/home/u", name)})", file.Json.Replace("\\\\", "\\", StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Fault_details_from_stderr_are_redacted()
    {
        using var tmp = new TempDir();
        var runner = new StubRunner(1, "", "auth failed for key sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWX and ghp_abcdefghijklmnopqrstuvwxyz0123456789");
        var def = new ExecutorDefinition { Cmd = ["agent"], Capabilities = new ExecutorCapabilities { Headless = true } };

        var result = await new ConfigExecutor("agent", def, new ExecutorSession { LogDirectory = Path.Combine(tmp.Path, "logs") }, runner)
            .RunAsync(new Brief { Prompt = "p", Body = "b" }, tmp.Path, null, TimeSpan.FromMinutes(1), default);

        Assert.Equal(WorkerFaultKind.ProcessFailed, result.Fault?.Kind);
        Assert.DoesNotContain("sk-ant-api03-ABCDEF", result.Fault!.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_abcdef", result.Fault.Detail, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.Fault.Detail, StringComparison.Ordinal);
    }

    private sealed class StubRunner(int exit, string stdout, string stderr) : IProcessRunner
    {
        public Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct) =>
            Task.FromResult(new ProcessOutcome { ExitCode = exit, TimedOut = false, Duration = TimeSpan.Zero, Stdout = stdout, Stderr = stderr });
    }
}

public class FeedbackRedactionTests
{
    [Fact]
    public void Gate_output_that_prints_an_oauth_credential_file_is_redacted_before_it_reaches_the_worker()
    {
        const string leaked = """
            FAIL test_x
            {"access_token": "ya29.a0AfB_byC1234567890abcdefghijklmnop", "refresh_token": "1//0gAbCdEfGhIjKlMnOpQrStUvWxYz", "client_secret": "GOCSPX-abc", "expiry": 1}
            """;
        var feedback = Tokonomy.Runtime.Executors.BriefWriter.Feedback("gate failed",
            [new GateResult { Gate = "test", Status = GateStatus.Failed, Output = leaked }]);

        Assert.DoesNotContain("ya29.a0AfB", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("1//0gAbCd", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("GOCSPX-abc", feedback, StringComparison.Ordinal);
        Assert.Contains("FAIL test_x", feedback, StringComparison.Ordinal);   // the useful part survives
    }
}
