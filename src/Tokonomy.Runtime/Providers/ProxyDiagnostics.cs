using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tokonomy.Core.Net;
using Tokonomy.Runtime.Config;

namespace Tokonomy.Runtime.Providers;

/// <summary>What one connection check found.</summary>
public sealed record ProbeResult(string Target, bool Ok, string Detail, TimeSpan Elapsed);

/// <summary>A place Tokonomy talks to, and how: its own HTTP client, or a child process using the proxy variables.</summary>
public sealed record ProxyTarget(string Id, string Title, string Url, ConnectionKind Kind, ApiService? Api = null);

/// <summary>
/// Checks that the configured proxy works and that each model or subscription Tokonomy uses can be reached through it.
/// A reachability check treats any HTTP answer (even 401 or 404) as "reachable": the point is the route, not the login.
/// </summary>
public static class ProxyDiagnostics
{
    private static ProxyTarget Claude { get; } = new("claude", "Claude Code subscription", "https://api.anthropic.com", ConnectionKind.ClaudeCode);

    private static ProxyTarget Antigravity { get; } = new("antigravity", "Antigravity subscription", "https://cloudcode-pa.googleapis.com", ConnectionKind.Antigravity);

    public static ProxyTarget Api(ApiService s) => new(s.Id, $"{s.Title} API", s.BaseUrl, ConnectionKind.Api, s);

    public static IReadOnlyList<ProxyTarget> AllTargets { get; } =
    [
        Claude, Antigravity, .. Sources.ApiServices.Select(Api),
    ];

    /// <summary>The connections this user actually set up (from the last setup), or everything when there is none.</summary>
    public static IReadOnlyList<ProxyTarget> UsedTargets(UserDefaults? defaults)
    {
        if (defaults is null)
        {
            return AllTargets;
        }

        var used = AllTargets.Where(t => t.Id.Equals(defaults.Strong, StringComparison.OrdinalIgnoreCase)
                                         || t.Id.Equals(defaults.Cheap, StringComparison.OrdinalIgnoreCase)).ToList();
        return used.Count == 0 ? AllTargets : used;
    }

    /// <summary>Can Tokonomy open a connection to the proxy itself?</summary>
    public static async Task<ProbeResult> ProbeProxyAsync(ProxySettings proxy, CancellationToken ct)
    {
        var (host, port, _) = proxy.Endpoint();
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return new ProbeResult($"proxy {host}:{port}", true, "accepting connections", watch.Elapsed);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return new ProbeResult($"proxy {host}:{port}", false,
                "nothing is listening there. Is the proxy app (V2RayN, ...) running, and is this its port?", watch.Elapsed);
        }
    }

    /// <summary>
    /// Reaches the target through the proxy. API services use Tokonomy's own client (and check the key when the variable
    /// is set); CLI subscriptions use the route the child process gets (the proxy variables, or the local bridge for SOCKS).
    /// </summary>
    public static async Task<ProbeResult> ProbeTargetAsync(ProxyTarget target, ProxySettings proxy, Func<string, string?>? getEnv, CancellationToken ct)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var watch = Stopwatch.StartNew();
        var route = proxy.IsActive ? "via proxy" : "direct";
        try
        {
            using var handler = CreateHandler(target, proxy);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

            if (target.Api is { } api && getEnv(api.KeyVariable) is { Length: > 0 } key)
            {
                var check = await ApiModels.CheckKeyAsync(http, api.BaseUrl, key, api.Id == "openrouter", ct);
                return new ProbeResult(target.Title, check.Ok, $"{route}: {check.Message}", watch.Elapsed);
            }

            using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, target.Url), HttpCompletionOption.ResponseHeadersRead, ct);
            var note = target.Api is { } missing ? $" ({missing.KeyVariable} is not set, so the key was not checked)" : "";
            return new ProbeResult(target.Title, true, $"{route}: reachable (HTTP {(int)response.StatusCode}){note}", watch.Elapsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return new ProbeResult(target.Title, false, $"{route}: {Describe(ex)}", watch.Elapsed);
        }
    }

    private static SocketsHttpHandler CreateHandler(ProxyTarget target, ProxySettings proxy)
    {
        if (target.Kind == ConnectionKind.Api || !proxy.IsActive)
        {
            return proxy.CreateHandler();
        }

        // A CLI tool gets the proxy through its environment, so test that exact route.
        var childUrl = proxy.ChildEnvironment()["HTTPS_PROXY"];
        return new SocketsHttpHandler { Proxy = new WebProxy(childUrl) { BypassProxyOnLocal = false }, UseProxy = true };
    }

    private static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null)
        {
            inner = inner.InnerException;
        }

        return ex is TaskCanceledException ? "no answer within 20 seconds" : inner.Message;
    }
}
