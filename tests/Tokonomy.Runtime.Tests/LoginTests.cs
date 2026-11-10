using Tokonomy.Core.Net;
using Tokonomy.Runtime.Config;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Tests;

public class LoginTests
{
    private static readonly ProxySettings Proxy = new() { Enabled = true, Url = "http://127.0.0.1:10809" };

    [Theory]
    [InlineData(ConnectionKind.ClaudeCode, "claude auth login")]
    [InlineData(ConnectionKind.Codex, "codex login")]
    [InlineData(ConnectionKind.Antigravity, "agy")]
    public void Each_cli_has_its_own_sign_in_command(ConnectionKind kind, string command) =>
        Assert.Equal(command, string.Join(' ', ToolLogin.PlanFor(kind)!.Args));

    [Fact]
    public void Codex_can_sign_in_with_a_device_code() =>
        Assert.Equal("codex login --device-auth", string.Join(' ', ToolLogin.PlanFor(ConnectionKind.Codex, deviceCode: true)!.Args));

    [Fact]
    public void An_api_connection_has_no_sign_in() => Assert.Null(ToolLogin.PlanFor(ConnectionKind.Api));

    [Fact]
    public void Any_child_process_gets_the_proxy_even_without_an_environment_of_its_own()
    {
        var psi = ProcessRunner.BuildStartInfo(new ProcessSpec { Args = [Shell], WorkingDirectory = Directory.GetCurrentDirectory() }, Proxy);
        Assert.Equal("http://127.0.0.1:10809", psi.Environment["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:10809", psi.Environment["ALL_PROXY"]);
        Assert.Contains("localhost", psi.Environment["NO_PROXY"]);
    }

    [Fact]
    public void A_spec_that_sets_a_proxy_variable_itself_keeps_its_value()
    {
        var spec = new ProcessSpec
        {
            Args = [Shell],
            WorkingDirectory = Directory.GetCurrentDirectory(),
            Environment = new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://own:1" },
        };
        var psi = ProcessRunner.BuildStartInfo(spec, Proxy);
        Assert.Equal("http://own:1", psi.Environment["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:10809", psi.Environment["HTTP_PROXY"]);
    }

    [Fact]
    public void Nothing_is_added_when_the_proxy_is_off()
    {
        var psi = ProcessRunner.BuildStartInfo(
            new ProcessSpec { Args = [Shell], WorkingDirectory = Directory.GetCurrentDirectory(), ReplaceEnvironment = true, Environment = new Dictionary<string, string> { ["PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? "", ["ComSpec"] = System.Environment.GetEnvironmentVariable("ComSpec") ?? "" } },
            ProxySettings.Off);
        Assert.DoesNotContain(psi.Environment.Keys, k => k.Contains("PROXY", StringComparison.OrdinalIgnoreCase));
    }

    private static string Shell => OperatingSystem.IsWindows() ? "cmd" : "sh";
}
