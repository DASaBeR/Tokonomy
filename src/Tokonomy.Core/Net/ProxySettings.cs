using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tokonomy.Core.Net;

/// <summary>
/// The user's outbound proxy (HTTP, HTTPS or SOCKS5), kept per user in <c>%LOCALAPPDATA%\tokonomy\proxy.json</c>.
/// When it is active every outbound call Tokonomy makes goes through it: its own HTTP clients (setup checks, API planning,
/// the metering proxy's upstream calls) and every child process it starts (Claude Code, Antigravity, git, gates, workers)
/// through the standard proxy variables. <c>TOKONOMY_PROXY</c> overrides the file (<c>off</c> disables it).
/// </summary>
public sealed partial class ProxySettings
{
    public const string EnvironmentVariable = "TOKONOMY_PROXY";

    private static readonly string[] AlwaysBypass = ["localhost", "127.0.0.1", "::1"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public bool Enabled { get; set; }

    /// <summary><c>scheme://[user:password@]host:port</c> with scheme http, https, socks5 or socks5h.</summary>
    public string Url { get; set; } = "";

    /// <summary>Hosts that skip the proxy (a host also covers its subdomains). Loopback always skips it.</summary>
    public List<string> NoProxy { get; set; } = [];

    public static ProxySettings Off { get; } = new();

    /// <summary>What every part of Tokonomy uses. Set once at startup by the CLI; it is <see cref="Off"/> until then (so tests never see the user's file).</summary>
    public static ProxySettings Current { get; set; } = Off;

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tokonomy", "proxy.json");

    [JsonIgnore]
    public bool IsActive => Enabled && TryNormalize(Url, out _, out _);

    /// <summary>Where the setting came from, for display.</summary>
    [JsonIgnore]
    public string Source { get; private set; } = "none";

    public static ProxySettings LoadUser(string? path = null, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var fromEnv = getEnv(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            fromEnv = fromEnv.Trim();
            if (fromEnv.Equals("off", StringComparison.OrdinalIgnoreCase) || fromEnv.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return new ProxySettings { Source = EnvironmentVariable };
            }

            if (TryNormalize(fromEnv, out var normalized, out _))
            {
                return new ProxySettings { Enabled = true, Url = normalized, Source = EnvironmentVariable };
            }
        }

        try
        {
            path ??= DefaultPath;
            if (File.Exists(path) && JsonSerializer.Deserialize<ProxySettings>(File.ReadAllText(path), Json) is { } loaded)
            {
                loaded.Source = path;
                return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new ProxySettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>
    /// Accepts <c>127.0.0.1:10808</c> (taken as http), or a full URL. Returns the canonical form
    /// <c>scheme://[user:password@]host:port</c>. A port is required.
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized, out string error)
    {
        normalized = "";
        input = input?.Trim() ?? "";
        if (input.Length == 0)
        {
            error = "the proxy address is empty";
            return false;
        }

        if (!input.Contains("://", StringComparison.Ordinal))
        {
            input = "http://" + input;
        }

        var match = UrlShape().Match(input);
        if (!match.Success)
        {
            error = "use scheme://host:port, e.g. socks5://127.0.0.1:10808 or http://127.0.0.1:10809 (a port is required)";
            return false;
        }

        var scheme = match.Groups["scheme"].Value.ToLowerInvariant();
        if (scheme is not ("http" or "https" or "socks5" or "socks5h"))
        {
            error = $"'{scheme}' is not supported; use http, https, socks5 or socks5h";
            return false;
        }

        if (!int.TryParse(match.Groups["port"].Value, out var port) || port is < 1 or > 65535)
        {
            error = "the port must be between 1 and 65535";
            return false;
        }

        var user = match.Groups["user"].Success ? "@" : "";
        normalized = $"{scheme}://{match.Groups["userinfo"].Value}{user}{match.Groups["host"].Value}:{port}";
        error = "";
        return true;
    }

    [JsonIgnore]
    public bool IsSocks => Scheme is "socks5" or "socks5h";

    private string Scheme => Url.Contains("://", StringComparison.Ordinal) ? Url[..Url.IndexOf("://", StringComparison.Ordinal)].ToLowerInvariant() : "";

    /// <summary>The proxy's host and port, and credentials if the URL has them.</summary>
    public (string Host, int Port, NetworkCredential? Credential) Endpoint()
    {
        if (!TryNormalize(Url, out var normalized, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var m = UrlShape().Match(normalized);
        var host = m.Groups["host"].Value.Trim('[', ']');
        NetworkCredential? credential = null;
        if (m.Groups["userinfo"].Value is { Length: > 0 } info)
        {
            var parts = info.Split(':', 2);
            credential = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }

        return (host, int.Parse(m.Groups["port"].Value), credential);
    }

    /// <summary>The URL with any password hidden, safe to print.</summary>
    [JsonIgnore]
    public string Display => UrlShape().Replace(Url, m => m.Groups["userinfo"].Success && m.Groups["userinfo"].Value.Length > 0
        ? $"{m.Groups["scheme"].Value}://{m.Groups["userinfo"].Value.Split(':')[0]}:***@{m.Groups["host"].Value}:{m.Groups["port"].Value}"
        : m.Value);

    /// <summary>A handler for Tokonomy's own HTTP clients: through the proxy when active, otherwise the .NET default.</summary>
    public SocketsHttpHandler CreateHandler(DecompressionMethods decompression = DecompressionMethods.None)
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = decompression };
        if (IsActive)
        {
            handler.Proxy = CreateWebProxy();
            handler.UseProxy = true;
        }

        return handler;
    }

    public IWebProxy CreateWebProxy()
    {
        var (host, port, credential) = Endpoint();
        var scheme = Scheme == "socks5h" ? "socks5" : Scheme; // .NET sends the host name to a SOCKS5 proxy either way
        var uri = new UriBuilder(scheme, host, port).Uri;
        return new WebProxy(uri, BypassOnLocal: true, BypassList(), credential);
    }

    /// <summary>
    /// The proxy variables for a child process. SOCKS is handed over as a local HTTP address (Claude Code, Node and most
    /// CLI tools cannot speak SOCKS), served by a small bridge that tunnels through the SOCKS proxy.
    /// </summary>
    public IReadOnlyDictionary<string, string> ChildEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!IsActive)
        {
            return env;
        }

        var url = IsSocks ? Socks5Bridge.GetOrStart(this).Url : Url;
        var noProxy = string.Join(",", AlwaysBypass.Concat(NoProxy).Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
        {
            env[name] = url;
            env[name.ToLowerInvariant()] = url;
        }

        env["NO_PROXY"] = noProxy;
        env["no_proxy"] = noProxy;
        return env;
    }

    /// <summary>Puts <see cref="ChildEnvironment"/> into <paramref name="env"/>, replacing proxy variables already there.</summary>
    public void Apply(IDictionary<string, string> env)
    {
        foreach (var (key, value) in ChildEnvironment())
        {
            env[key] = value;
        }
    }

    private string[] BypassList() =>
        NoProxy.Select(h => h.Trim().TrimStart('*').TrimStart('.')).Where(h => h.Length > 0)
            .Select(h => @"^[a-z0-9+.\-]+://(?:[^/:]+\.)?" + Regex.Escape(h) + @"(?::\d+)?$")
            .ToArray();

    [GeneratedRegex(@"^(?<scheme>[a-z][a-z0-9+.\-]*)://(?:(?<userinfo>[^@/\s]*)(?<user>@))?(?<host>\[[^\]]+\]|[^:/@\s]+):(?<port>\d{1,5})/?$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlShape();
}
