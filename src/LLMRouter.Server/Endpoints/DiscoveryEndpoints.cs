using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-085: provider discovery — scan/results/verify. STRICT loopback-only
/// surface (upstream api/discovery/*/route.ts): scans may probe outbound
/// endpoints (SSRF-adjacent), so the routes reject non-local clients even
/// though they also require admin auth. Local providers scanned: ollama
/// (11434), llama.cpp/llamafile (8080), lmstudio (1234), vllm (8000) — plus a
/// stub row for remote providers (upstream scanProvider Phase-1 stub).
/// </summary>
public static class DiscoveryEndpoints
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Well-known local provider ports and their models endpoint.</summary>
    static readonly (string provider, string url, int feasibility)[] LocalTargets =
    [
        ("ollama", "http://127.0.0.1:11434/api/tags", 5),
        ("llamacpp", "http://127.0.0.1:8080/v1/models", 4),
        ("lmstudio", "http://127.0.0.1:1234/v1/models", 4),
        ("vllm", "http://127.0.0.1:8000/v1/models", 4),
    ];

    static bool IsLoopback(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        // null = in-process host (TestServer / unix socket) — loopback-equivalent
        return ip is null || System.Net.IPAddress.IsLoopback(ip)
            || ip.ToString() is "::1" or "127.0.0.1";
    }

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/discovery").RequireAuthorization();

        g.MapPost("/scan", async (HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hcf, CancellationToken ct) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "loopback only" }, JsonOpts, statusCode: 403);
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var providerId = b.GetProperty("providerId").GetString()!.ToLowerInvariant();

            var results = new List<DiscoveryResult>();
            var target = LocalTargets.FirstOrDefault(t => t.provider == providerId);
            if (target.url is not null)
            {
                // real local probe (upstream probeEndpoint)
                var accessible = false; string[] models = [];
                try
                {
                    using var resp = await hcf.CreateClient("logexport")
                        .GetAsync(target.url, ct);
                    accessible = resp.IsSuccessStatusCode;
                    if (accessible)
                    {
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                        var arr = doc.RootElement.TryGetProperty("models", out var mm) ? mm
                            : doc.RootElement.TryGetProperty("data", out var dd) ? dd
                            : doc.RootElement.TryGetProperty("models", out _) ? mm : default;
                        if (arr.ValueKind == JsonValueKind.Array)
                            models = arr.EnumerateArray().Take(50)
                                .Select(e => e.TryGetProperty("id", out var i) ? i.GetString() ?? ""
                                    : e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                                .Where(s => s.Length > 0).ToArray();
                    }
                }
                catch { /* not running locally */ }
                results.Add(Upsert(db, new DiscoveryResult
                {
                    ProviderId = providerId, Method = "public_api", Endpoint = target.url,
                    AuthType = "none", Models = JsonSerializer.Serialize(models),
                    Feasibility = accessible ? target.feasibility : 1,
                    RiskLevel = "none", Status = accessible ? "testing" : "rejected",
                    Notes = accessible ? $"local probe ok — {models.Length} models" : "local probe unreachable",
                }));
            }
            else
            {
                // upstream stub for providers without a known local probe
                results.Add(Upsert(db, new DiscoveryResult
                {
                    ProviderId = providerId, Method = "free_tier", AuthType = "none",
                    Feasibility = 3, RiskLevel = "none", Status = "pending",
                    Notes = "Stub scan — no known local probe for this provider",
                }));
            }
            await db.SaveChangesAsync(ct);
            return Results.Json(new { results }, JsonOpts);
        });

        g.MapGet("/results", async (HttpContext ctx, LlmRouterDbContext db, string? providerId) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "loopback only" }, JsonOpts, statusCode: 403);
            var q = db.DiscoveryResults.AsNoTracking().AsQueryable();
            if (providerId is not null) q = q.Where(r => r.ProviderId == providerId);
            return Results.Json(new { results = await q.OrderByDescending(r => r.DiscoveredAt).ToListAsync() }, JsonOpts);
        });

        g.MapPost("/verify/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "loopback only" }, JsonOpts, statusCode: 403);
            var r = await db.DiscoveryResults.FindAsync(id);
            if (r is null) return Results.NotFound();
            r.Status = "verified"; r.VerifiedAt = Now();
            await db.SaveChangesAsync();
            return Results.Json(new { result = r }, JsonOpts);
        });

        g.MapDelete("/results/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            if (!IsLoopback(ctx)) return Results.Json(new { error = "loopback only" }, JsonOpts, statusCode: 403);
            var r = await db.DiscoveryResults.FindAsync(id);
            if (r is not null) { db.DiscoveryResults.Remove(r); await db.SaveChangesAsync(); }
            return Results.Json(new { ok = true }, JsonOpts);
        });
    }

    /// <summary>Uniqueness keyed on (providerId, method, endpoint) — upstream upsert.</summary>
    static DiscoveryResult Upsert(LlmRouterDbContext db, DiscoveryResult candidate)
    {
        var existing = db.DiscoveryResults.Local.FirstOrDefault(r =>
            r.ProviderId == candidate.ProviderId && r.Method == candidate.Method
            && r.Endpoint == candidate.Endpoint)
            ?? db.DiscoveryResults.FirstOrDefault(r =>
                r.ProviderId == candidate.ProviderId && r.Method == candidate.Method
                && r.Endpoint == candidate.Endpoint);
        if (existing is null)
        {
            candidate.Id = Guid.NewGuid().ToString("N")[..12];
            candidate.DiscoveredAt = Now();
            db.DiscoveryResults.Add(candidate);
            return candidate;
        }
        existing.Status = candidate.Status;
        existing.Models = candidate.Models;
        existing.Feasibility = candidate.Feasibility;
        existing.Notes = candidate.Notes;
        return existing;
    }
}
