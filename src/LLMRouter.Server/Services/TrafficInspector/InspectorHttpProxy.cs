using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LLMRouter.Server.Services.TrafficInspector;

/// <summary>
/// HTTP_PROXY capture listener — port of mitm/inspector/httpProxyServer.ts.
/// Plain-HTTP absolute-URI requests are forwarded and fully captured;
/// CONNECT tunnels get a raw relay and a metadata-only entry with the
/// upstream "bodies opaque" note. Port conflicts surface as EADDRINUSE.
/// </summary>
public sealed class InspectorHttpProxy : IAsyncDisposable
{
    private const string HopNote = "CONNECT tunnel — bodies are opaque (no TLS intercept in http-proxy mode)";
    private readonly TrafficBuffer _buffer;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public int Port { get; private set; }
    public bool Running => _listener is not null;

    public InspectorHttpProxy(TrafficBuffer buffer) => _buffer = buffer;

    /// <summary>Throws SocketException (AddrInUse) like upstream's EADDRINUSE.</summary>
    public Task Start(int port)
    {
        if (_listener is not null) return Task.CompletedTask;
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start(); // SocketException propagates → 409 mapping in endpoint
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _loop = AcceptLoop(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task Stop()
    {
        if (_listener is null) return;
        if (_cts is not null) await _cts.CancelAsync();
        _listener.Stop();
        try { await (_loop ?? Task.CompletedTask); }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await Stop();

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(async () =>
            {
                try { await Handle(client, ct); }
                catch (Exception) { /* per-connection failure is not fatal */ }
                finally { client.Dispose(); }
            }, ct);
        }
    }

    private async Task Handle(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var (head, body) = await ReadHead(stream, ct);
        if (head is null) return;
        var headText = Encoding.ASCII.GetString(head);
        var requestLine = headText[..headText.IndexOf('\n')].TrimEnd('\r');
        var parts = requestLine.Split(' ', 3);
        if (parts.Length < 2) return;
        var headers = ParseHeaders(headText);

        if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            var authority = parts[1].Split(' ')[0];
            var host = authority.Split(':')[0];
            var port = authority.Contains(':') && int.TryParse(authority.Split(':')[1], out var p) ? p : 443;
            await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);
            _buffer.Push(new InterceptedRequest
            {
                Id = Guid.NewGuid().ToString(),
                Source = "http-proxy",
                Timestamp = DateTime.UtcNow.ToString("o"),
                Method = "CONNECT", Host = host, Path = $":{port}",
                RequestHeaders = InspectorModel.SanitizeHeaders(headers),
                RequestSize = head.Length, ResponseSize = 0, Status = 200,
                Note = HopNote,
            });
            await Relay(stream, host, port, ct);
            return;
        }

        // Read the content-length remainder beyond what arrived with the head.
        if (headers.TryGetValue("content-length", out var clv)
            && int.TryParse(clv, out var cl) && cl > (body?.Length ?? 0))
        {
            var full = new byte[cl];
            var have = body?.Length ?? 0;
            if (have > 0) Array.Copy(body!, full, have);
            var off = have;
            while (off < cl)
            {
                var n = await stream.ReadAsync(full.AsMemory(off, cl - off), ct);
                if (n <= 0) break;
                off += n;
            }
            body = off > 0 ? full[..off] : body;
        }
        await ForwardPlain(stream, parts, headers, body, head.Length, ct);
    }

    /// <summary>Non-CONNECT: forward to http://{host}{path} and capture the full exchange.</summary>
    private async Task ForwardPlain(NetworkStream stream, string[] parts,
        Dictionary<string, string> headers, byte[]? body, int headBytes, CancellationToken ct)
    {
        var (method, rawPath) = (parts[0], parts[1]);
        if (!headers.TryGetValue("host", out var hostHeader) || hostHeader is null) return;
        var rawHost = hostHeader.Split(':')[0];
        var path = rawPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(rawPath).PathAndQuery : rawPath;
        var url = $"http://{hostHeader}{path}";
        var started = System.Diagnostics.Stopwatch.StartNew();
        var entry = new InterceptedRequest
        {
            Id = Guid.NewGuid().ToString(),
            Source = "http-proxy",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Method = method, Host = rawHost, Path = path,
            RequestHeaders = InspectorModel.SanitizeHeaders(headers),
            RequestBody = body is null ? null : MaskSecrets.Mask(Encoding.UTF8.GetString(body)),
            RequestSize = headBytes + (body?.Length ?? 0),
            Status = "in-flight",
        };
        _buffer.Push(entry);
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromMinutes(5) };
            var req = new HttpRequestMessage(new HttpMethod(method), url);
            foreach (var (k, v) in headers)
            {
                if (k is "host" or "connection" or "content-length" or "keep-alive"
                    or "proxy-authenticate" or "proxy-authorization" or "proxy-connection"
                    or "te" or "trailer" or "transfer-encoding" or "upgrade")
                    continue;
                req.Headers.TryAddWithoutValidation(k, v);
            }
            if (body is not null) req.Content = new ByteArrayContent(body);
            using var resp = await http.SendAsync(req, ct);
            var respBody = await resp.Content.ReadAsByteArrayAsync(ct);
            var respHeaders = resp.Headers.Concat(resp.Content.Headers)
                .Where(h => h.Key is not ("transfer-encoding" or "connection"))
                .Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)))
                .ToList();
            entry.Status = (int)resp.StatusCode;
            entry.ResponseHeaders = InspectorModel.SanitizeHeaders(respHeaders);
            entry.ResponseBody = MaskSecrets.Mask(Encoding.UTF8.GetString(respBody));
            entry.ResponseSize = respBody.Length;
            entry.UpstreamLatencyMs = started.ElapsedMilliseconds;
            entry.TotalLatencyMs = started.ElapsedMilliseconds;
            _buffer.Update(entry.Id, entry);
            var sb = new StringBuilder($"HTTP/1.1 {(int)resp.StatusCode} OK\r\n");
            foreach (var (k, v) in respHeaders)
                if (!k.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                    sb.Append(k).Append(": ").Append(v).Append("\r\n");
            sb.Append("Content-Length: ").Append(respBody.Length).Append("\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
            await stream.WriteAsync(respBody, ct);
        }
        catch (Exception e)
        {
            entry.Status = "error";
            entry.Error = MaskSecrets.Mask(e.Message);
            _buffer.Update(entry.Id, entry);
        }
    }

    private static async Task Relay(Stream client, string host, int port, CancellationToken ct)
    {
        using var upstream = new TcpClient();
        await upstream.ConnectAsync(host, port, ct);
        var up = upstream.GetStream();
        await Task.WhenAny(Copy(client, up, ct), Copy(up, client, ct));
        static async Task Copy(Stream a, Stream b, CancellationToken ct)
        {
            var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int n;
                while ((n = await a.ReadAsync(buf, ct)) > 0)
                    await b.WriteAsync(buf.AsMemory(0, n), ct);
            }
            catch { /* relay termination is expected */ }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
    }

    private static async Task<(byte[]? head, byte[]? body)> ReadHead(NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        var delim = "\r\n\r\n"u8.ToArray();
        while (ms.Length < 64 * 1024)
        {
            var n = await stream.ReadAsync(buf, ct);
            if (n <= 0) return (null, null);
            await ms.WriteAsync(buf.AsMemory(0, n), ct);
            var all = ms.ToArray();
            var idx = all.AsSpan().IndexOf(delim);
            if (idx >= 0)
            {
                var rest = all[(idx + 4)..];
                return (all[..(idx + 4)], rest.Length > 0 ? rest : null);
            }
        }
        return (null, null);
    }

    private static Dictionary<string, string> ParseHeaders(string headText)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in headText.Split("\r\n").Skip(1))
        {
            var i = line.IndexOf(':');
            if (i > 0) d[line[..i].Trim().ToLowerInvariant()] = line[(i + 1)..].Trim();
        }
        return d;
    }
}
