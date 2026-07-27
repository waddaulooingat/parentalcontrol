using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ParentalGuard.Core;

/// <summary>
/// Local forward proxy listening on 127.0.0.1:8877. Browsers point their proxy settings
/// here; plain HTTP requests and HTTPS CONNECT tunnels are checked against the
/// <see cref="BlocklistManager"/> before being allowed through.
/// </summary>
/// <remarks>
/// Built on a raw <see cref="TcpListener"/> rather than <see cref="HttpListener"/> because
/// HttpListener has no support for the CONNECT verb, which is required to see (and block by
/// domain) HTTPS tunnels without terminating TLS.
/// </remarks>
public class ProxyEngine : IDisposable
{
    private readonly int _port;
    private readonly BlocklistManager _blocklist;
    private readonly Logger _logger;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public ProxyEngine(BlocklistManager blocklist, Logger logger, int port = 8877)
    {
        _blocklist = blocklist;
        _logger = logger;
        _port = port;
    }

    public bool IsRunning => _listener != null;

    public void Start()
    {
        if (_listener != null) return;

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = HandleClientAsync(client, token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using var _ = client;

        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();

            var requestLine = await ReadLineAsync(stream, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(requestLine)) return;

            var headers = new List<string>();
            string? line;
            while (!string.IsNullOrEmpty(line = await ReadLineAsync(stream, token).ConfigureAwait(false)))
            {
                headers.Add(line);
            }

            var parts = requestLine.Split(' ', 3);
            if (parts.Length < 3) return;

            var method = parts[0];
            var target = parts[1];

            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await HandleConnectAsync(stream, target, token).ConfigureAwait(false);
            }
            else
            {
                await HandlePlainHttpAsync(stream, method, target, headers, token).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HandleConnectAsync(NetworkStream clientStream, string target, CancellationToken token)
    {
        var (host, port) = SplitHostPort(target, 443);
        var blocked = _blocklist.IsBlocked(host, string.Empty);
        _logger.LogRequest(host, string.Empty, blocked);

        if (blocked)
        {
            await WriteAsync(clientStream, "HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n", token).ConfigureAwait(false);
            return;
        }

        TcpClient upstream;
        try
        {
            upstream = new TcpClient();
            await upstream.ConnectAsync(host, port, token).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            await WriteAsync(clientStream, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n", token).ConfigureAwait(false);
            return;
        }

        using (upstream)
        {
            await WriteAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n", token).ConfigureAwait(false);

            using var upstreamStream = upstream.GetStream();
            var clientToUpstream = clientStream.CopyToAsync(upstreamStream, token);
            var upstreamToClient = upstreamStream.CopyToAsync(clientStream, token);
            await Task.WhenAny(clientToUpstream, upstreamToClient).ConfigureAwait(false);
        }
    }

    private async Task HandlePlainHttpAsync(
        NetworkStream clientStream, string method, string target, List<string> headers, CancellationToken token)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            await WriteAsync(clientStream, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n", token).ConfigureAwait(false);
            return;
        }

        var blocked = _blocklist.IsBlocked(uri.Host, uri.PathAndQuery);
        _logger.LogRequest(uri.Host, uri.PathAndQuery, blocked);

        if (blocked)
        {
            var body = BuildBlockPageHtml(uri.Host);
            var response =
                "HTTP/1.1 403 Forbidden\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                "Connection: close\r\n\r\n" +
                body;
            await WriteAsync(clientStream, response, token).ConfigureAwait(false);
            return;
        }

        try
        {
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(uri.Host, uri.IsDefaultPort ? 80 : uri.Port, token).ConfigureAwait(false);
            using var upstreamStream = upstream.GetStream();

            var forwardedRequest = new StringBuilder();
            forwardedRequest.Append(method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
            foreach (var header in headers)
            {
                if (header.StartsWith("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) continue;
                forwardedRequest.Append(header).Append("\r\n");
            }

            forwardedRequest.Append("\r\n");

            await WriteAsync(upstreamStream, forwardedRequest.ToString(), token).ConfigureAwait(false);
            await upstreamStream.CopyToAsync(clientStream, token).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            await WriteAsync(clientStream, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n", token).ConfigureAwait(false);
        }
    }

    private static string BuildBlockPageHtml(string host) =>
        $"""
        <html>
        <head><title>Blocked</title></head>
        <body style="font-family: sans-serif; text-align: center; margin-top: 15%;">
        <h1>Access to {WebUtility.HtmlEncode(host)} is blocked</h1>
        <p>Parental Guard has blocked this site.</p>
        </body>
        </html>
        """;

    private static (string Host, int Port) SplitHostPort(string target, int defaultPort)
    {
        var idx = target.LastIndexOf(':');
        if (idx < 0) return (target, defaultPort);

        var host = target[..idx];
        return int.TryParse(target[(idx + 1)..], out var port) ? (host, port) : (host, defaultPort);
    }

    private static async Task WriteAsync(Stream stream, string text, CancellationToken token)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var buffer = new List<byte>();
        var singleByte = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(singleByte, token).ConfigureAwait(false);
            if (read == 0) return buffer.Count == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());

            if (singleByte[0] == '\n')
            {
                if (buffer.Count > 0 && buffer[^1] == '\r') buffer.RemoveAt(buffer.Count - 1);
                return Encoding.ASCII.GetString(buffer.ToArray());
            }

            buffer.Add(singleByte[0]);
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
