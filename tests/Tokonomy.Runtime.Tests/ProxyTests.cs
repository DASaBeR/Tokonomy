using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Tokonomy.Core.Net;
using Tokonomy.Runtime.Process;
using Tokonomy.Runtime.Providers;

namespace Tokonomy.Runtime.Tests;

public class ProxyTests
{
    [Theory]
    [InlineData("127.0.0.1:10808", "http://127.0.0.1:10808")]
    [InlineData("SOCKS5://127.0.0.1:10808", "socks5://127.0.0.1:10808")]
    [InlineData("http://bob:pw@proxy.corp:8080", "http://bob:pw@proxy.corp:8080")]
    [InlineData("socks5h://[::1]:1080", "socks5h://[::1]:1080")]
    public void Accepts_and_normalizes(string input, string expected)
    {
        Assert.True(ProxySettings.TryNormalize(input, out var normalized, out _));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("socks4://127.0.0.1:1080")]
    [InlineData("127.0.0.1:99999")]
    public void Rejects_bad_addresses(string input) => Assert.False(ProxySettings.TryNormalize(input, out _, out _));

    [Fact]
    public void Display_hides_the_password() =>
        Assert.Equal("http://bob:***@proxy:8080", new ProxySettings { Enabled = true, Url = "http://bob:pw@proxy:8080" }.Display);

    [Fact]
    public void Environment_variable_overrides_the_file_and_off_disables()
    {
        using var dir = new TempDir();
        var path = dir.File("proxy.json", """{"enabled": true, "url": "http://127.0.0.1:1111"}""");
        Assert.Equal("http://127.0.0.1:1111", ProxySettings.LoadUser(path, _ => null).Url);
        Assert.Equal("socks5://127.0.0.1:2222", ProxySettings.LoadUser(path, _ => "socks5://127.0.0.1:2222").Url);
        Assert.False(ProxySettings.LoadUser(path, _ => "off").IsActive);
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "p", "proxy.json");
        new ProxySettings { Enabled = true, Url = "http://127.0.0.1:10809", NoProxy = ["corp.example"] }.Save(path);
        var loaded = ProxySettings.LoadUser(path, _ => null);
        Assert.True(loaded.IsActive);
        Assert.Equal(["corp.example"], loaded.NoProxy);
    }

    [Fact]
    public void Children_get_the_proxy_even_with_credentials_and_workers_too()
    {
        var parent = new Hashtable { ["PATH"] = "C:/bin", ["HTTPS_PROXY"] = "http://old:secret@elsewhere:1" };
        var proxy = new ProxySettings { Enabled = true, Url = "http://alice:pa55@127.0.0.1:10809", NoProxy = ["corp.example"] };

        var gate = EnvironmentBuilder.ForGate([], parent, proxy);
        Assert.Equal("http://alice:pa55@127.0.0.1:10809", gate["HTTPS_PROXY"]);
        Assert.Equal("http://alice:pa55@127.0.0.1:10809", gate["ALL_PROXY"]);
        Assert.Contains("127.0.0.1", gate["NO_PROXY"]);
        Assert.Contains("corp.example", gate["NO_PROXY"]);

        var worker = EnvironmentBuilder.ForWorker([], new Dictionary<string, string>(), parent, proxy);
        Assert.Equal("http://alice:pa55@127.0.0.1:10809", worker["HTTP_PROXY"]);
    }

    [Fact]
    public void Nothing_is_added_when_off()
    {
        var env = EnvironmentBuilder.ForGate([], new Hashtable { ["PATH"] = "x" }, ProxySettings.Off);
        Assert.DoesNotContain(env.Keys, k => k.Contains("PROXY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Socks_proxy_is_handed_to_children_as_a_working_local_http_bridge()
    {
        using var socks = new FakeSocks5();
        var proxy = new ProxySettings { Enabled = true, Url = $"socks5://127.0.0.1:{socks.Port}" };
        var bridge = proxy.ChildEnvironment()["HTTPS_PROXY"];
        Assert.StartsWith("http://127.0.0.1:", bridge);

        var uri = new Uri(bridge);
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("CONNECT api.example.com:443 HTTP/1.1\r\nHost: api.example.com:443\r\n\r\n"));
        var reply = new byte[39];
        var read = 0;
        while (read < 39)
        {
            read += await stream.ReadAsync(reply.AsMemory(read));
        }

        Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(reply));

        await stream.WriteAsync(Encoding.ASCII.GetBytes("ping"));
        var echo = new byte[4];
        await stream.ReadExactlyAsync(echo);
        Assert.Equal("ping", Encoding.ASCII.GetString(echo));
        Assert.Equal("api.example.com:443", await socks.Requested.Task);
    }

    [Fact]
    public async Task Probe_reports_a_dead_proxy_and_a_live_one()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var live = new ProxySettings { Enabled = true, Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}" };
        Assert.True((await ProxyDiagnostics.ProbeProxyAsync(live, CancellationToken.None)).Ok);
        listener.Stop();
        Assert.False((await ProxyDiagnostics.ProbeProxyAsync(live, CancellationToken.None)).Ok);
    }

    [Fact]
    public void Handler_uses_the_proxy_only_when_active()
    {
        Assert.Null(ProxySettings.Off.CreateHandler().Proxy);
        var handler = new ProxySettings { Enabled = true, Url = "socks5://127.0.0.1:10808" }.CreateHandler();
        Assert.NotNull(handler.Proxy);
        Assert.Equal("socks5://127.0.0.1:10808/", handler.Proxy!.GetProxy(new Uri("https://api.anthropic.com"))!.ToString());
        Assert.True(handler.Proxy.IsBypassed(new Uri("http://127.0.0.1:5000")));
    }

    /// <summary>No-auth SOCKS5 server: accepts one CONNECT by name, then echoes.</summary>
    private sealed class FakeSocks5 : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public FakeSocks5()
        {
            _listener.Start();
            _ = Task.Run(Serve);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public TaskCompletionSource<string> Requested { get; } = new();

        private async Task Serve()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var s = client.GetStream();
            var greeting = new byte[3];
            await s.ReadExactlyAsync(greeting);
            await s.WriteAsync(new byte[] { 5, 0 });
            var head = new byte[5];
            await s.ReadExactlyAsync(head);
            var name = new byte[head[4] + 2];
            await s.ReadExactlyAsync(name);
            Requested.TrySetResult($"{Encoding.ASCII.GetString(name, 0, head[4])}:{(name[^2] << 8) | name[^1]}");
            await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            var buffer = new byte[256];
            int n;
            while ((n = await s.ReadAsync(buffer)) > 0)
            {
                await s.WriteAsync(buffer.AsMemory(0, n));
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
