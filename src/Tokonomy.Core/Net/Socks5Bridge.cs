using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Tokonomy.Core.Net;

/// <summary>
/// A loopback HTTP proxy (CONNECT only, so HTTPS) that tunnels through a SOCKS5 proxy. Child tools such as Claude Code
/// understand <c>HTTPS_PROXY=http://...</c> but not SOCKS, so they are pointed at this and reach the user's SOCKS proxy
/// (V2RayN and similar) through it. It lives as long as the process.
/// </summary>
public sealed class Socks5Bridge : IDisposable
{
    private static readonly ConcurrentDictionary<string, Socks5Bridge> Bridges = new(StringComparer.Ordinal);

    private readonly TcpListener _listener;
    private readonly string _host;
    private readonly int _port;
    private readonly System.Net.NetworkCredential? _credential;
    private readonly CancellationTokenSource _stop = new();

    private Socks5Bridge(string host, int port, System.Net.NetworkCredential? credential)
    {
        _host = host;
        _port = port;
        _credential = credential;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The local HTTP proxy address to hand to child processes.</summary>
    public string Url { get; }

    public static Socks5Bridge GetOrStart(ProxySettings settings)
    {
        var (host, port, credential) = settings.Endpoint();
        return Bridges.GetOrAdd($"{host}:{port}:{credential?.UserName}", _ => new Socks5Bridge(host, port, credential));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    /// <summary>Opens a TCP tunnel to <paramref name="host"/>:<paramref name="port"/> through a SOCKS5 proxy (the name is resolved by the proxy).</summary>
    public static async Task<TcpClient> ConnectThroughAsync(
        string proxyHost, int proxyPort, System.Net.NetworkCredential? credential, string host, int port, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(proxyHost, proxyPort, ct);
            var stream = client.GetStream();

            await stream.WriteAsync(credential is null ? new byte[] { 5, 1, 0 } : new byte[] { 5, 2, 0, 2 }, ct);
            var choice = await ReadExactAsync(stream, 2, ct);
            if (choice[0] != 5)
            {
                throw new IOException("the proxy did not answer as a SOCKS5 server");
            }

            if (choice[1] == 2)
            {
                if (credential is null)
                {
                    throw new IOException("the SOCKS5 proxy wants a user name and password");
                }

                var user = Encoding.UTF8.GetBytes(credential.UserName);
                var pass = Encoding.UTF8.GetBytes(credential.Password);
                await stream.WriteAsync(new byte[] { 1, (byte)user.Length }.Concat(user).Append((byte)pass.Length).Concat(pass).ToArray(), ct);
                if ((await ReadExactAsync(stream, 2, ct))[1] != 0)
                {
                    throw new IOException("the SOCKS5 proxy rejected the user name or password");
                }
            }
            else if (choice[1] != 0)
            {
                throw new IOException("the SOCKS5 proxy accepts no sign-in method Tokonomy supports");
            }

            var request = new List<byte> { 5, 1, 0 };
            if (IPAddress.TryParse(host, out var ip))
            {
                request.Add((byte)(ip.AddressFamily == AddressFamily.InterNetwork ? 1 : 4));
                request.AddRange(ip.GetAddressBytes());
            }
            else
            {
                var name = Encoding.ASCII.GetBytes(host);
                request.Add(3);
                request.Add((byte)name.Length);
                request.AddRange(name);
            }

            request.Add((byte)(port >> 8));
            request.Add((byte)(port & 0xFF));
            await stream.WriteAsync(request.ToArray(), ct);

            var head = await ReadExactAsync(stream, 4, ct);
            if (head[1] != 0)
            {
                throw new IOException($"the SOCKS5 proxy could not reach {host}:{port} ({ReplyText(head[1])})");
            }

            var rest = head[3] switch
            {
                1 => 6,
                4 => 18,
                3 => (await ReadExactAsync(stream, 1, ct))[0] + 2,
                _ => throw new IOException("the SOCKS5 proxy sent an unknown address type"),
            };
            await ReadExactAsync(stream, rest, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static string ReplyText(byte code) => code switch
    {
        1 => "general failure",
        2 => "not allowed by the proxy's rules",
        3 => "network unreachable",
        4 => "host unreachable",
        5 => "connection refused",
        6 => "TTL expired",
        7 => "command not supported",
        8 => "address type not supported",
        _ => $"code {code}",
    };

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0)
            {
                throw new IOException("the proxy closed the connection");
            }

            read += n;
        }

        return buffer;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var local = client;
        try
        {
            local.NoDelay = true;
            var stream = local.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            // Read the request head (up to the blank line); anything after it already belongs to the tunnel.
            var buffer = new byte[16 * 1024];
            var filled = 0;
            var headEnd = -1;
            while (headEnd < 0)
            {
                if (filled == buffer.Length)
                {
                    return;
                }

                var n = await stream.ReadAsync(buffer.AsMemory(filled), timeout.Token);
                if (n == 0)
                {
                    return;
                }

                filled += n;
                headEnd = Encoding.ASCII.GetString(buffer, 0, filled).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }

            var headText = Encoding.ASCII.GetString(buffer, 0, headEnd);
            var requestLine = headText.Split("\r\n")[0].Split(' ');
            if (requestLine.Length < 2 || !requestLine[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(stream, "HTTP/1.1 501 Not Implemented\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }

            var target = requestLine[1];
            var colon = target.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(target[(colon + 1)..], out var port))
            {
                await WriteTextAsync(stream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }

            TcpClient upstream;
            try
            {
                upstream = await ConnectThroughAsync(_host, _port, _credential, target[..colon].Trim('[', ']'), port, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                await WriteTextAsync(stream, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }

            using (upstream)
            {
                await WriteTextAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n");
                var upstreamStream = upstream.GetStream();
                var early = headEnd + 4;
                if (filled > early)
                {
                    await upstreamStream.WriteAsync(buffer.AsMemory(early, filled - early), _stop.Token);
                }

                var up = stream.CopyToAsync(upstreamStream, _stop.Token);
                var down = upstreamStream.CopyToAsync(stream, _stop.Token);
                await Task.WhenAny(up, down);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static Task WriteTextAsync(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();
}
