using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMRouter.Server.Services.AgentBridge;

/// <summary>
/// MITM proxy engine — a .NET port of OmniRoute's mitm server (server.cjs +
/// manager.ts): a CONNECT-aware TCP proxy that routes each connection through
/// bypass > target > passthrough, terminates TLS for target hosts with a
/// per-host cert signed by the gateway's root CA, rewrites the model via the
/// agent's mappings, injects the internal API key, and forwards to the
/// gateway's own /v1 endpoints. Non-target traffic is relayed transparently.
/// </summary>
public sealed class MitmProxy : IAsyncDisposable
{
    private readonly MitmCa _ca;
    private readonly Func<string, Task<IReadOnlyList<string>>> _bypassProvider;
    private readonly Func<string, Task<IReadOnlyDictionary<string, string>>> _mappingsProvider;
    private readonly Func<Task<string?>> _apiKeyProvider;
    private readonly string _forwardBase;
    private readonly ILogger? _log;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _active;

    private long _total;
    private long _intercepted;
    private long _passthrough;
    private volatile Action<TrafficInspector.InterceptedRequest>? _capture;
    public int Port { get; private set; }
    public bool Running => _listener is not null;
    public int ActiveConnections => _active;
    public long TotalConnections => _total;
    public long Intercepted => _intercepted;
    public long Passthrough => _passthrough;

    public MitmProxy(MitmCa ca, string forwardBase,
        Func<string, Task<IReadOnlyList<string>>>? bypassProvider = null,
        Func<string, Task<IReadOnlyDictionary<string, string>>>? mappingsProvider = null,
        Func<Task<string?>>? apiKeyProvider = null,
        ILogger? log = null)
    {
        _ca = ca;
        _forwardBase = forwardBase.TrimEnd('/');
        _bypassProvider = bypassProvider ?? (_ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));
        _mappingsProvider = mappingsProvider ?? (_ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));
        _apiKeyProvider = apiKeyProvider ?? (() => Task.FromResult<string?>(null));
        _log = log;
    }

    /// <summary>Traffic Inspector capture hook — invoked per intercepted request.</summary>
    public void SetCapture(Action<TrafficInspector.InterceptedRequest>? capture)
        => _capture = capture;

    public Task Start(int port = 8888)
    {
        if (_listener is not null) return Task.CompletedTask;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptLoop = AcceptLoop(_cts.Token);
        _log?.LogInformation("mitm proxy listening on :{Port}", Port);
        return Task.CompletedTask;
    }

    public async Task Stop()
    {
        if (_listener is null) return;
        if (_cts is not null) await _cts.CancelAsync();
        _listener.Stop();
        try { await (_acceptLoop ?? Task.CompletedTask); }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _log?.LogInformation("mitm proxy stopped");
    }

    public async ValueTask DisposeAsync() => await Stop();

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }
            Interlocked.Increment(ref _total);
            _ = Task.Run(async () =>
            {
                Interlocked.Increment(ref _active);
                try { await Handle(client, ct); }
                catch (Exception e) { _log?.LogDebug(e, "mitm connection handler failed"); }
                finally { Interlocked.Decrement(ref _active); client.Dispose(); }
            }, ct);
        }
    }

    private async Task Handle(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var reader = new BufferedReader(stream);
        // Peek the first line: CONNECT authority-form or a plain HTTP request.
        var head = await reader.ReadUntil("\r\n\r\n"u8.ToArray(), 16 * 1024, ct);
        if (head is null) return;
        var headText = Encoding.ASCII.GetString(head.Value.head);
        var requestLine = headText[..headText.IndexOf('\n')].TrimEnd('\r');

        if (requestLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
        {
            var authority = requestLine[8..].Split(' ')[0];
            var host = authority.Split(':')[0];
            var port = authority.Contains(':') && int.TryParse(authority.Split(':')[1], out var p) ? p : 443;
            var bypass = await _bypassProvider(host);
            var (kind, target) = AgentBridgeTargets.RouteConnection(host, bypass);

            if (kind is "bypass" or "passthrough")
            {
                Interlocked.Increment(ref _passthrough);
                _capture?.Invoke(new TrafficInspector.InterceptedRequest
                {
                    Id = Guid.NewGuid().ToString(),
                    Source = "agent-bridge",
                    Agent = kind == "passthrough" ? target?.Name : null,
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    Method = "CONNECT", Host = host, Path = $":{port}",
                    RequestHeaders = TrafficInspector.InspectorModel.SanitizeHeaders(ParseHeaders(headText)),
                    RequestSize = head.Value.head.Length, ResponseSize = 0, Status = 200,
                    Note = "CONNECT tunnel — bodies are opaque (no TLS intercept for this host)",
                });
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);
                await RelayTcp(stream, host, port, ct);
                return;
            }

            // Target: 200 → TLS terminate with signed leaf → intercept HTTP.
            Interlocked.Increment(ref _intercepted);
            await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);
            var leaf = _ca.LeafFor(host);
            var tls = new SslStream(stream, leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = leaf,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            }, ct);
            await InterceptHttp(new BufferedReader(tls), tls, target!, host, ct);
            return;
        }

        // Plain HTTP proxying: absolute-URI requests forwarded to gateway.
        await InterceptPlainHttp(reader, stream, headText, ct);
    }

    /// <summary>TCP passthrough: connect to the real host and relay both ways.</summary>
    private static async Task RelayTcp(Stream client, string host, int port, CancellationToken ct)
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
            catch { /* relay termination (peer close or cancel) is expected */ }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
    }

    private const string ModelKey = "model";

    /// <summary>Intercept HTTP/1.1 requests inside a terminated TLS stream.</summary>
    private async Task InterceptHttp(BufferedReader reader, SslStream tls, MitmTarget target, string host, CancellationToken ct)
    {
        var mappings = await _mappingsProvider(target.Id);
        var apiKey = await _apiKeyProvider();
        while (!ct.IsCancellationRequested)
        {
            var read = await reader.ReadUntil("\r\n\r\n"u8.ToArray(), 64 * 1024, ct);
            if (read is null) return;
            var headText = Encoding.ASCII.GetString(read.Value.head);
            var parts = headText[..headText.IndexOf('\n')].TrimEnd('\r').Split(' ', 3);
            if (parts.Length < 2) return;
            var (method, rawPath) = (parts[0], parts[1]);
            var path = rawPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new Uri(rawPath).PathAndQuery : rawPath;

            var headers = ParseHeaders(headText);
            var body = await ReadBody(reader, headers, ct);

            var entry = NewEntry(target, host, method, path, headers, body, read.Value.head.Length);
            _capture?.Invoke(entry);
            var started = System.Diagnostics.Stopwatch.StartNew();

            body = ApplyMapping(mappings, body, entry);

            int status; List<KeyValuePair<string, string>> respHeaders; byte[] respBody;
            try { (status, respHeaders, respBody) = await Forward(method, path, headers, body, apiKey, ct); }
            catch (Exception e)
            {
                entry.Status = "error";
                entry.Error = TrafficInspector.MaskSecrets.Mask(e.Message);
                entry.TotalLatencyMs = started.ElapsedMilliseconds;
                _capture?.Invoke(entry);
                throw;
            }
            CompleteEntry(entry, status, respHeaders, respBody, started);
            await WriteResponse(tls, status, respHeaders, respBody, ct);
            if (!headers.TryGetValue("connection", out var conn) ||
                !conn.Equals("keep-alive", StringComparison.OrdinalIgnoreCase) && conn?.Equals("close", StringComparison.OrdinalIgnoreCase) == true)
                return;
        }
    }

    private TrafficInspector.InterceptedRequest NewEntry(MitmTarget target, string host, string method,
        string path, Dictionary<string, string> headers, byte[]? body, int headBytes)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            Source = "agent-bridge",
            Agent = target.Name,
            Timestamp = DateTime.UtcNow.ToString("o"),
            Method = method, Host = host, Path = path,
            RequestHeaders = TrafficInspector.InspectorModel.SanitizeHeaders(headers),
            RequestBody = body is null ? null
                : TrafficInspector.MaskSecrets.Mask(Encoding.UTF8.GetString(body)),
            RequestSize = headBytes + (body?.Length ?? 0),
            Status = "in-flight",
        };

    /// <summary>Model mapping (upstream handler contract: source_model → target_model).</summary>
    private static byte[]? ApplyMapping(IReadOnlyDictionary<string, string> mappings, byte[]? body,
        TrafficInspector.InterceptedRequest entry)
    {
        if (mappings.Count == 0 || body is null) return body;
        var mapped = TryMapModel(body, mappings);
        if (ReferenceEquals(mapped, body)) return body;
        try
        {
            entry.SourceModel = (JsonNode.Parse(body) as JsonObject)?[ModelKey]?.GetValue<string>();
            entry.MappedModel = (JsonNode.Parse(mapped!) as JsonObject)?[ModelKey]?.GetValue<string>();
        }
        catch (JsonException) { /* mapping bookkeeping is best-effort */ }
        return mapped;
    }

    private void CompleteEntry(TrafficInspector.InterceptedRequest entry, int status,
        List<KeyValuePair<string, string>> respHeaders, byte[] respBody, System.Diagnostics.Stopwatch started)
    {
        entry.Status = status;
        entry.ResponseHeaders = TrafficInspector.InspectorModel.SanitizeHeaders(respHeaders);
        entry.ResponseBody = TrafficInspector.MaskSecrets.Mask(Encoding.UTF8.GetString(respBody));
        entry.ResponseSize = respBody.Length;
        entry.UpstreamLatencyMs = started.ElapsedMilliseconds;
        entry.TotalLatencyMs = started.ElapsedMilliseconds;
        _capture?.Invoke(entry);
    }

    private async Task InterceptPlainHttp(BufferedReader reader, NetworkStream stream, string headText, CancellationToken ct)
    {
        var parts = headText[..headText.IndexOf('\n')].TrimEnd('\r').Split(' ', 3);
        if (parts.Length < 2) return;
        var (method, rawPath) = (parts[0], parts[1]);
        var path = rawPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(rawPath).PathAndQuery : rawPath;
        var headers = ParseHeaders(headText);
        var body = await ReadBody(reader, headers, ct);
        var apiKey = await _apiKeyProvider();
        var (status, respHeaders, respBody) = await Forward(method, path, headers, body, apiKey, ct);
        await WriteResponse(stream, status, respHeaders, respBody, ct);
    }

    private async Task<(int status, List<KeyValuePair<string, string>> headers, byte[] body)>
        Forward(string method, string path, Dictionary<string, string> headers, byte[]? body, string? apiKey, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        var req = new HttpRequestMessage(new HttpMethod(method), _forwardBase + path);
        foreach (var (k, v) in headers)
        {
            if (k is "host" or "connection" or "content-length" or "transfer-encoding" or "keep-alive" or "proxy-connection")
                continue;
            req.Headers.TryAddWithoutValidation(k, v);
        }
        if (!headers.ContainsKey("authorization") && apiKey is not null)
            req.Headers.TryAddWithoutValidation("authorization", $"Bearer {apiKey}");
        if (body is not null)
            req.Content = new ByteArrayContent(body);

        using var resp = await http.SendAsync(req, ct);
        var respHeaders = resp.Headers.Concat(resp.Content.Headers)
            .Where(h => h.Key is not ("transfer-encoding" or "connection"))
            .Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)))
            .ToList();
        return ((int)resp.StatusCode, respHeaders, await resp.Content.ReadAsByteArrayAsync(ct));
    }

    private static async Task WriteResponse(Stream s, int status, List<KeyValuePair<string, string>> headers, byte[] body, CancellationToken ct)
    {
        var sb = new StringBuilder($"HTTP/1.1 {status} {StatusText(status)}\r\n");
        foreach (var (k, v) in headers)
            if (!k.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        await s.WriteAsync(body, ct);
    }

    private static byte[]? TryMapModel(byte[] body, IReadOnlyDictionary<string, string> mappings)
    {
        try
        {
            var node = JsonNode.Parse(body) as JsonObject;
            if (node?[ModelKey]?.GetValue<string>() is { } src &&
                mappings.TryGetValue(src, out var dst))
            {
                node[ModelKey] = dst;
                return Encoding.UTF8.GetBytes(node.ToJsonString());
            }
        }
        catch (JsonException) { /* malformed body — forward unmapped */ }
        return body;
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

    private static async Task<byte[]?> ReadBody(BufferedReader reader, Dictionary<string, string> headers, CancellationToken ct)
    {
        if (headers.TryGetValue("content-length", out var clv) && long.TryParse(clv, out var cl) && cl > 0)
            return await reader.ReadExact((int)cl, ct);
        if (headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var sizeLine = await reader.ReadUntil("\r\n"u8.ToArray(), 64, ct);
                if (sizeLine is null) break;
                var size = Convert.ToInt32(Encoding.ASCII.GetString(sizeLine.Value.head).Trim(), 16);
                if (size == 0) { await reader.ReadUntil("\r\n"u8.ToArray(), 4096, ct); break; }
                var chunk = await reader.ReadExact(size + 2, ct);
                if (chunk is null) break;
                await ms.WriteAsync(chunk.AsMemory(0, size), ct);
            }
            return ms.ToArray();
        }
        return null;
    }

    /// <summary>
    /// Per-connection buffered reader: ReadUntil returns the bytes up to the
    /// delimiter and KEEPS whatever came after it for the next call — network
    /// reads are not aligned to HTTP message boundaries.
    /// </summary>
    private sealed class BufferedReader
    {
        private readonly Stream _s;
        private readonly MemoryStream _carry = new();
        private readonly byte[] _buf = new byte[8192];

        public BufferedReader(Stream s) => _s = s;

        public async Task<(byte[] head, byte[] leftover)?> ReadUntil(byte[] delim, int max, CancellationToken ct)
        {
            while (true)
            {
                var cur = _carry.ToArray();
                var idx = cur.AsSpan().IndexOf(delim);
                if (idx >= 0)
                {
                    _carry.SetLength(0);
                    await _carry.WriteAsync(cur.AsMemory(idx + delim.Length), ct);
                    return (cur[..(idx + delim.Length)], cur[(idx + delim.Length)..]);
                }
                if (cur.Length >= max) return null;
                var n = await _s.ReadAsync(_buf, ct);
                if (n <= 0) return null;
                await _carry.WriteAsync(_buf.AsMemory(0, n), ct);
            }
        }

        public async Task<byte[]?> ReadExact(int count, CancellationToken ct)
        {
            var cur = _carry.ToArray();
            if (cur.Length >= count)
            {
                _carry.SetLength(0);
                await _carry.WriteAsync(cur.AsMemory(count), ct);
                return cur[..count];
            }
            using var ms = new MemoryStream();
            await ms.WriteAsync(cur, ct);
            _carry.SetLength(0);
            while (ms.Length < count)
            {
                var n = await _s.ReadAsync(_buf, ct);
                if (n <= 0) return null;
                await ms.WriteAsync(_buf.AsMemory(0, n), ct);
            }
            var all = ms.ToArray();
            if (all.Length > count)
                await _carry.WriteAsync(all.AsMemory(count), ct);
            return all[..count];
        }
    }

    private static string StatusText(int code) => code switch
    {
        200 => "OK", 201 => "Created", 204 => "No Content",
        400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
        429 => "Too Many Requests", 500 => "Internal Server Error", 502 => "Bad Gateway",
        503 => "Service Unavailable", _ => "Status",
    };
}
