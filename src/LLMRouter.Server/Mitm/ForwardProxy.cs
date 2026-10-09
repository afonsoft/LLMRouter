using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using LLMRouter.Core.Mitm;

namespace LLMRouter.Server.Mitm;

/// <summary>
/// SPEC-014: forward-proxy middleware — accepts absolute-URI requests
/// (GET http://host/path), forwards upstream and captures the flow.
/// CONNECT is intentionally not implemented (plain forward proxy only).
/// </summary>
public static class ForwardProxy
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>True when the request is a forward-proxy (absolute-URI) request.</summary>
    /// <summary>Absolute-URI target (forward-proxy form) — Path or RawTarget.</summary>
    public static bool IsProxyRequest(HttpRequest req)
    {
        var raw = req.HttpContext.Features
            .Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget
            ?? req.Path.Value ?? "";
        return raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task Invoke(HttpContext ctx, RequestDelegate next)
    {
        if (!IsProxyRequest(ctx.Request))
        {
            // CONNECT host:port → 501 (not implemented — cert-free proxy only)
            if (ctx.Request.Method == "CONNECT")
            {
                ctx.Response.StatusCode = 501;
                await ctx.Response.WriteAsync("CONNECT not supported — use plain forward proxy");
                return;
            }
            await next(ctx);
            return;
        }

        // SPEC-023: when apiKeys exist, proxy traffic must authenticate via
        // Proxy-Authorization (Basic user:sk-... — password carries the key —
        // or Bearer sk-...)
        var db = ctx.RequestServices.GetService<LLMRouter.Core.Data.LlmRouterDbContext>();
        if (db is not null && await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .AnyAsync(db.ApiKeys, k => k.IsActive))
        {
            var cred = ExtractProxyKey(ctx.Request.Headers.ProxyAuthorization);
            var valid = cred is not null && await Microsoft.EntityFrameworkCore
                .EntityFrameworkQueryableExtensions
                .AnyAsync(db.ApiKeys, k => k.Key == cred && k.IsActive);
            if (!valid)
            {
                ctx.Response.StatusCode = 407;
                ctx.Response.Headers.ProxyAuthenticate = "Basic realm=llmrouter";
                await ctx.Response.WriteAsync("Proxy authentication required");
                return;
            }
        }

        var target = ctx.Features
            .Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget
            ?? ctx.Request.Path.Value!;
        if (!target.Contains('?')) target += ctx.Request.QueryString;
        var sw = Stopwatch.StartNew();
        var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);
        foreach (var h in ctx.Request.Headers)
        {
            if (h.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                || h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (!req.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray()))
                req.Content ??= new StreamContent(ctx.Request.Body);
        }
        if (ctx.Request.ContentLength > 0)
        {
            req.Content = new StreamContent(ctx.Request.Body);
            if (ctx.Request.ContentType is { } ct)
                req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ct);
        }

        var sw2 = sw;
        try
        {
            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
            ctx.Response.StatusCode = (int)resp.StatusCode;
            foreach (var h in resp.Headers.Concat(resp.Content.Headers))
                ctx.Response.Headers[h.Key] = h.Value.ToArray();
            var body = await resp.Content.ReadAsByteArrayAsync();
            await ctx.Response.Body.WriteAsync(body);
            Capture(ctx, target, req, resp, body, (int)sw2.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsync($"proxy error: {ex.Message}");
            if (TrafficCapture.Enabled)
                TrafficCapture.Add(new(Guid.NewGuid().ToString("N")[..10], DateTime.UtcNow,
                    ctx.Request.Method, new Uri(target).Host, new Uri(target).PathAndQuery,
                    502, (int)sw2.ElapsedMilliseconds,
                    (int)(ctx.Request.ContentLength ?? 0), 0,
                    string.Join("\n", ctx.Request.Headers.Select(h => $"{h.Key}: {h.Value}")), null,
                    "", ex.Message));
        }
    }

    private static string? ExtractProxyKey(string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header[7..].Trim();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
                var idx = decoded.IndexOf(':');
                // Basic user:pass — the password carries the API key
                return idx >= 0 ? decoded[(idx + 1)..] : decoded;
            }
            catch { return null; }
        }
        return header;
    }

    private static void Capture(HttpContext ctx, string target, HttpRequestMessage req,
        HttpResponseMessage resp, byte[] body, int ms)
    {
        if (!TrafficCapture.Enabled) return;
        var uri = new Uri(target);
        TrafficCapture.Add(new(Guid.NewGuid().ToString("N")[..10], DateTime.UtcNow,
            ctx.Request.Method, uri.Host, uri.PathAndQuery, (int)resp.StatusCode, ms,
            (int)(ctx.Request.ContentLength ?? 0), body.Length,
            string.Join("\n", req.Headers.Select(h => $"{h.Key}: {string.Join(",", h.Value)}")),
            ctx.Request.ContentLength is > 0 and < 65_536 ? "(streamed)" : null,
            string.Join("\n", resp.Headers.Concat(resp.Content.Headers).Select(h => $"{h.Key}: {string.Join(",", h.Value)}")),
            body.Length is > 0 and < 65_536
                ? System.Text.Encoding.UTF8.GetString(body) : $"<{body.Length} bytes>"));
    }

    private static X509Certificate2? _ca;

    /// <summary>Self-signed CA for HTTPS interception opt-in (download + install).</summary>
    public static X509Certificate2 GetOrCreateCa()
    {
        if (_ca is not null) return _ca;
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new CertificateRequest("CN=LLMRouter Local CA", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        _ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return _ca;
    }
}
