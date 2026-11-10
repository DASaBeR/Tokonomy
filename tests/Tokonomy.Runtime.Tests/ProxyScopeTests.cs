using System.Net;
using Tokonomy.Core.Net;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Process;
using Tokonomy.Runtime.Providers;

namespace Tokonomy.Runtime.Tests;

/// <summary>The proxy used only for chosen connections, for example OpenRouter through V2Ray and Codex direct.</summary>
public class ProxyScopeTests
{
    private static ProxySettings Only(params string[] ids) =>
        new() { Enabled = true, Url = "http://127.0.0.1:10809", Scope = [.. ids] };

    [Fact]
    public void No_scope_means_everything()
    {
        var all = Only();
        Assert.True(all.AppliesTo("codex"));
        Assert.True(all.AppliesTo(ProxySettings.OtherId));
        Assert.Same(all, all.ForProvider("claude"));
    }

    [Fact]
    public void A_scope_limits_the_proxy_to_the_listed_connections()
    {
        var scoped = Only("openrouter");
        Assert.True(scoped.AppliesTo("OpenRouter"));
        Assert.False(scoped.AppliesTo("codex"));
        Assert.False(scoped.AppliesTo(ProxySettings.OtherId));
        Assert.Same(ProxySettings.Off, scoped.ForProvider("codex"));
        Assert.False(scoped.ForProvider("codex").IsActive);
    }

    [Fact]
    public void Child_processes_of_out_of_scope_connections_get_no_proxy_variables()
    {
        var scoped = Only("openrouter");
        Assert.Empty(scoped.ForProvider("codex").ChildEnvironment());
        Assert.Equal("http://127.0.0.1:10809", scoped.ForProvider("openrouter").ChildEnvironment()["HTTPS_PROXY"]);
    }

    [Fact]
    public void Http_calls_go_through_the_proxy_only_for_hosts_of_connections_in_scope()
    {
        var proxy = Only("openrouter").CreateHandler().Proxy!;
        Assert.False(proxy.IsBypassed(new Uri("https://openrouter.ai/api/v1/models")));
        Assert.NotEqual(new Uri("https://openrouter.ai/api/v1/models"), proxy.GetProxy(new Uri("https://openrouter.ai/api/v1/models")));
        Assert.True(proxy.IsBypassed(new Uri("https://api.openai.com/v1/models")));
        Assert.True(proxy.IsBypassed(new Uri("https://api.avalai.ir/v1/models")));
    }

    [Fact]
    public void A_custom_provider_in_scope_is_proxied_by_its_unknown_host()
    {
        var proxy = Only("custom").CreateHandler().Proxy!;
        Assert.False(proxy.IsBypassed(new Uri("https://api.avalai.ir/v1/models")));
        Assert.True(proxy.IsBypassed(new Uri("https://openrouter.ai/api/v1/models")));
    }

    [Theory]
    [InlineData("openrouter.ai", "openrouter")]
    [InlineData("api.anthropic.com", "anthropic")]
    [InlineData("generativelanguage.googleapis.com", "gemini")]
    [InlineData("api.openai.com", "openai")]
    [InlineData("api.deepseek.com", "deepseek")]
    [InlineData("api.avalai.ir", "custom")]
    [InlineData("localhost", "custom")]
    public void Hosts_map_to_connections(string host, string id) => Assert.Equal(id, ProxySettings.ProviderIdForHost(host));

    [Fact]
    public void A_spec_gets_its_own_connections_proxy_and_unmarked_tools_follow_the_other_setting()
    {
        var scoped = Only("codex");

        var codex = ProcessRunner.BuildStartInfo(new ProcessSpec { Args = [Shell], WorkingDirectory = Directory.GetCurrentDirectory(), Proxy = scoped.ForProvider("codex") }, scoped);
        Assert.Equal("http://127.0.0.1:10809", codex.Environment["HTTPS_PROXY"]);

        var claude = ProcessRunner.BuildStartInfo(new ProcessSpec { Args = [Shell], WorkingDirectory = Directory.GetCurrentDirectory(), Proxy = scoped.ForProvider("claude"), ReplaceEnvironment = true, Environment = MinimalEnv() }, scoped);
        Assert.DoesNotContain(claude.Environment.Keys, k => k.Contains("PROXY", StringComparison.OrdinalIgnoreCase));

        var git = ProcessRunner.BuildStartInfo(new ProcessSpec { Args = [Shell], WorkingDirectory = Directory.GetCurrentDirectory(), ReplaceEnvironment = true, Environment = MinimalEnv() }, scoped);
        Assert.DoesNotContain(git.Environment.Keys, k => k.Contains("PROXY", StringComparison.OrdinalIgnoreCase));

        var everything = Only("codex", ProxySettings.OtherId);
        var gitToo = ProcessRunner.BuildStartInfo(new ProcessSpec { Args = [Shell], WorkingDirectory = Directory.GetCurrentDirectory(), ReplaceEnvironment = true, Environment = MinimalEnv() }, everything);
        Assert.Equal("http://127.0.0.1:10809", gitToo.Environment["HTTPS_PROXY"]);
    }

    [Theory]
    [InlineData("claude", "claude")]
    [InlineData("C:/tools/codex.cmd", "codex")]
    [InlineData("agy", "antigravity")]
    [InlineData("opencode", "other")]
    public void A_worker_executor_is_matched_to_its_connection_by_the_command(string command, string id) =>
        Assert.Equal(id, ConfigExecutor.ProxyIdOf(new Tokonomy.Core.Config.ExecutorDefinition { Cmd = [command, "run"] }));

    [Fact]
    public void The_scope_is_saved_and_loaded()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "proxy.json");
        Only("openrouter", "other").Save(path);
        var loaded = ProxySettings.LoadUser(path, _ => null);
        Assert.Equal(["openrouter", "other"], loaded.Scope);
        Assert.Equal("openrouter, git, tests and other tools", loaded.ScopeDisplay);
        Assert.Equal("everything", Only().ScopeDisplay);
    }

    [Fact]
    public async Task The_connection_check_says_when_a_target_is_outside_the_scope()
    {
        // A local address, so no real traffic: the point is the route it reports.
        var target = new ProxyTarget("codex", "Codex", "http://127.0.0.1:1", ConnectionKind.Codex);
        var result = await ProxyDiagnostics.ProbeTargetAsync(target, Only("openrouter"), null, CancellationToken.None);
        Assert.Contains("not in the proxy's scope", result.Detail);
    }

    private static Dictionary<string, string> MinimalEnv() => new()
    {
        ["PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? "",
        ["ComSpec"] = System.Environment.GetEnvironmentVariable("ComSpec") ?? "",
    };

    private static string Shell => OperatingSystem.IsWindows() ? "cmd" : "sh";
}
