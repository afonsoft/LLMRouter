using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Mitm;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Mitm;

/// <summary>
/// SPEC-028: CONNECT proxy — standalone TcpListener (LLMROUTER_PROXY_PORT,
/// default 8889) handling `CONNECT host:port`:
///  - blind TCP tunnel by default,
///  - TLS interception when settings.mitm.tlsIntercept: per-host cert signed
///    by the local CA (ForwardProxy.GetOrCreateCa), decrypted traffic fed
///    into TrafficCapture for the inspector.
/// </summary>
public sealed class ConnectProxy : BackgroundService
{
    private readonly IServiceScopeFactory _sf;
    private readonly int _port;
    public ConnectProxy(IServiceScopeFactory sf)
    {
        _sf = sf;
        _port = int.TryParse(Environment.GetEnvironmentVariable("LLMROUTER_PROXY_PORT"), out var p) ? p : 8889;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        try { listener.Start(); }
        catch (SocketException) { return; } // port busy — feature off
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { break; }
                _ = Task.Run(() => HandleAsync(client, ct), ct);
            }
        }
        finally { listener.Stop(); }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        // read request line + headers
        var head = await ReadHeadersAsync(stream, ct);
        if (head is null) return;
        var line = head.Value.line;
        if (!line.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync("HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), ct);
            return;
        }
        var authority = line[8..].Split(' ')[0];
        var hp = authority.Split(':');
        var host = hp[0];
        var port = hp.Length > 1 && int.TryParse(hp[1], out var pp) ? pp : 443;

        var intercept = await TlsInterceptEnabledAsync(ct);
        await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);

        if (!intercept)
        {
            await TunnelAsync(stream, host, port, ct);
            return;
        }
        await InterceptAsync(stream, host, port, head.Value.headers, ct);
    }

    private static async Task TunnelAsync(NetworkStream client, string host, int port, CancellationToken ct)
    {
        try
        {
            using var remote = new TcpClient();
            await remote.ConnectAsync(host, port, ct);
            var rs = remote.GetStream();
            var c2r = PumpAsync(client, rs, ct);
            var r2c = PumpAsync(rs, client, ct);
            await Task.WhenAny(c2r, r2c);
        }
        catch { }
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int n;
            while ((n = await from.ReadAsync(buf, ct)) > 0)
                await to.WriteAsync(buf.AsMemory(0, n), ct);
        }
        catch { }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private async Task InterceptAsync(NetworkStream clientRaw, string host, int port,
        string headers, CancellationToken ct)
    {
        // server-side TLS to the client, cert minted for `host` by our CA
        using var server = new SslStream(clientRaw, false);
        try
        {
            await server.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = CertForHost(host),
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                    | System.Security.Authentication.SslProtocols.Tls13,
            }, ct);
        }
        catch { return; } // client doesn't trust CA — nothing to decrypt

        // loop HTTP/1.1 requests on the decrypted stream, forward, relay, capture
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(2) };
        var buf = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var req = await ReadRequestAsync(server, buf, ct);
                if (req is null) break;
                var (method, path, reqHeaders, body, keepAlive) = req.Value;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                HttpResponseMessage resp;
                try
                {
                    using var msg = new HttpRequestMessage(new HttpMethod(method), $"https://{host}{path}");
                    foreach (var h in reqHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var c = h.IndexOf(':');
                        if (c <= 0) continue;
                        var name = h[..c].Trim();
                        if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!msg.Headers.TryAddWithoutValidation(name, h[(c + 1)..].Trim()))
                            msg.Content ??= new ByteArrayContent(body);
                    }
                    if (body.Length > 0) msg.Content = new ByteArrayContent(body);
                    resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
                    var respBody = await resp.Content.ReadAsByteArrayAsync(ct);
                    var sb = new StringBuilder();
                    sb.Append($"HTTP/1.1 {(int)resp.StatusCode} {resp.ReasonPhrase}\r\n");
                    foreach (var h in resp.Headers.Concat(resp.Content.Headers))
                        if (!h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                            && !h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                            && !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                            sb.Append($"{h.Key}: {string.Join(",", h.Value)}\r\n");
                    sb.Append($"Content-Length: {respBody.Length}\r\nConnection: keep-alive\r\n\r\n");
                    var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
                    await server.WriteAsync(headBytes, ct);
                    await server.WriteAsync(respBody, ct);
                    if (TrafficCapture.Enabled)
                        TrafficCapture.Add(new(Guid.NewGuid().ToString("N")[..10], DateTime.UtcNow,
                            method, host, path, (int)resp.StatusCode, (int)sw.ElapsedMilliseconds,
                            body.Length, respBody.Length,
                            headers + reqHeaders,
                            body.Length is > 0 and < 65_536 ? Encoding.UTF8.GetString(body) : null,
                            string.Join("\n", resp.Headers.Concat(resp.Content.Headers)
                                .Select(h => $"{h.Key}: {string.Join(",", h.Value)}")),
                            respBody.Length is > 0 and < 65_536
                                ? Encoding.UTF8.GetString(respBody) : $"<{respBody.Length} bytes>"));
                    if (!keepAlive) break;
                }
                catch (Exception ex)
                {
                    var err = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 502 Bad Gateway\r\nContent-Length: {ex.Message.Length}\r\n\r\n{ex.Message}");
                    await server.WriteAsync(err, ct);
                    break;
                }
            }
        }
        catch { }
    }

    private static async Task<(string method, string path, string headers, byte[] body, bool keepAlive)?>
        ReadRequestAsync(SslStream s, MemoryStream carry, CancellationToken ct)
    {
        // simple HTTP/1.1 parser: headers to \r\n\r\n, then Content-Length body
        var tmp = new byte[8192];
        string? headerText = null; int headerEnd = -1;
        while (headerEnd < 0)
        {
            var n = await s.ReadAsync(tmp, ct);
            if (n <= 0) return null;
            carry.Write(tmp, 0, n);
            headerText = Encoding.Latin1.GetString(carry.GetBuffer(), 0, (int)carry.Length);
            headerEnd = headerText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }
        var headPart = headerText[..headerEnd];
        var lines = headPart.Split("\r\n");
        var parts = lines[0].Split(' ');
        var method = parts[0]; var path = parts[1];
        var keepAlive = !lines.Skip(1).Any(l => l.StartsWith("Connection: close", StringComparison.OrdinalIgnoreCase));
        var cl = 0;
        foreach (var l in lines.Skip(1))
            if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(l[15..].Trim(), out cl);
        var bodyStart = headerEnd + 4;
        var body = new byte[cl];
        var have = (int)carry.Length - bodyStart;
        var copied = Math.Min(have, cl);
        if (copied > 0) Array.Copy(carry.GetBuffer(), bodyStart, body, 0, copied);
        var got = copied;
        while (got < cl)
        {
            var n = await s.ReadAsync(tmp, ct);
            if (n <= 0) break;
            var take = Math.Min(n, cl - got);
            Array.Copy(tmp, 0, body, got, take);
            got += take;
        }
        carry.SetLength(0);
        return (method, path, string.Join("\n", lines.Skip(1)), body, keepAlive);
    }

    private static async Task<(string line, string headers)?> ReadHeadersAsync(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[4096];
        var ms = new MemoryStream();
        while (ms.Length < 64 * 1024)
        {
            var n = await s.ReadAsync(buf, ct);
            if (n <= 0) return null;
            ms.Write(buf, 0, n);
            var txt = Encoding.Latin1.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            var end = txt.IndexOf("\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                var line = txt[..end];
                var hdrEnd = txt.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                return (line, hdrEnd >= 0 ? txt[..hdrEnd] : txt);
            }
        }
        return null;
    }

    private async Task<bool> TlsInterceptEnabledAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _sf.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
            var row = await db.Settings.FindAsync([1], ct);
            if (row?.Data is null) return false;
            var d = JsonDocument.Parse(row.Data).RootElement;
            return d.TryGetProperty("mitm", out var m)
                && m.TryGetProperty("tlsIntercept", out var t)
                && t.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, X509Certificate2> Certs = new();

    /// <summary>Per-host leaf cert signed by the local CA (cached).</summary>
    public static X509Certificate2 CertForHost(string host) =>
        Certs.GetOrAdd(host, h =>
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest($"CN={h}", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(h);
            req.CertificateExtensions.Add(san.Build());
            var ca = ForwardProxy.GetOrCreateCa();
            var cert = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(2), RandomNumberGenerator.GetBytes(16));
            // re-attach private key: Create returns cert without the key
            return cert.CopyWithPrivateKey(rsa);
        });
}
