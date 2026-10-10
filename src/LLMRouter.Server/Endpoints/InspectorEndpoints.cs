using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LLMRouter.Core.Mitm;
using LLMRouter.Server.Mitm;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-014: inspector flows + relay + mitm CA download.</summary>
public static class InspectorEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void MapInspectorEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/inspector/flows", (string? host, string? q) =>
        {
            var flows = TrafficCapture.List().AsEnumerable();
            if (host is not null) flows = flows.Where(f => f.Host.Contains(host, StringComparison.OrdinalIgnoreCase));
            if (q is not null) flows = flows.Where(f => f.Path.Contains(q, StringComparison.OrdinalIgnoreCase));
            return Results.Json(new
            {
                enabled = TrafficCapture.Enabled,
                flows = flows.Select(f => new { f.Id, f.At, f.Method, f.Host, f.Path, f.Status, f.Ms, f.ReqBytes, f.RespBytes }),
            }, JsonOpts);
        });

        g.MapGet("/inspector/flows/{id}", (string id) =>
        {
            var f = TrafficCapture.Find(id);
            return f is null ? Results.NotFound() : Results.Json(f, JsonOpts);
        });

        g.MapPost("/inspector/capture", async (HttpContext ctx) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("enabled", out var e)) TrafficCapture.Enabled = e.ValueKind == JsonValueKind.True;
            if (b.TryGetProperty("clear", out var c) && c.ValueKind == JsonValueKind.True) TrafficCapture.Clear();
            return Results.Json(new { enabled = TrafficCapture.Enabled }, JsonOpts);
        });

        // Replay a captured flow against a different target URL (relay)
        g.MapPost("/inspector/relay", async (HttpContext ctx, IHttpClientFactory hf) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var id = b.GetProperty("flowId").GetString()!;
            var target = b.TryGetProperty("target", out var t) ? t.GetString() : null;
            var f = TrafficCapture.Find(id);
            if (f is null) return Results.NotFound(new { error = "flow not found" });
            var url = target is not null ? $"{target.TrimEnd('/')}{f.Path}" : $"https://{f.Host}{f.Path}";
            var c = hf.CreateClient("upstream");
            var req = new HttpRequestMessage(new HttpMethod(f.Method), url);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var body = await resp.Content.ReadAsStringAsync();
            return Results.Json(new
            {
                status = (int)resp.StatusCode,
                ms = sw.ElapsedMilliseconds,
                body = body[..Math.Min(20_000, body.Length)],
                originalStatus = f.Status,
                originalBody = f.RespBody,
            }, JsonOpts);
        });

        // Self-signed CA for HTTPS interception (install to trust)
        g.MapGet("/mitm/status", () => Results.Json(new
        {
            enabled = Environment.GetEnvironmentVariable("LLMROUTER_MITM") == "1",
            ca = "/api/mitm/ca",
        }, JsonOpts));

        g.MapGet("/mitm/ca", () =>
        {
            var ca = ForwardProxy.GetOrCreateCa();
            return Results.File(ca.Export(X509ContentType.Cert), "application/x-x509-ca-cert",
                "llmrouter-ca.crt");
        });
    }
}
