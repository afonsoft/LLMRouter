using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-084: intelligence + adaptive-routing endpoints —
/// /api/intelligence/* (arena_elo sync + user overrides) and
/// /api/routing/explain (adaptiveRouting explanations).
/// </summary>
public static class IntelligenceEndpoints
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // POST /api/intelligence/sync — run an Arena ELO sync now
        g.MapPost("/intelligence/sync", async (LlmRouterDbContext db, IHttpClientFactory hcf, CancellationToken ct) =>
        {
            var (models, errors) = await ArenaEloSync.SyncAsync(db, hcf.CreateClient("logexport"), ct);
            return Results.Json(new { synced = models, errors }, JsonOpts);
        });

        // GET /api/intelligence — current intelligence entries grouped by source
        g.MapGet("/intelligence", async (LlmRouterDbContext db, string? source, string? model) =>
        {
            var q = db.Kv.AsNoTracking().Where(k => k.Scope == ModelIntelligence.Scope);
            var rows = await q.ToListAsync();
            var entries = rows.Select(r =>
            {
                var parts = r.Key.Split(':', 3);
                return new
                {
                    source = parts.ElementAtOrDefault(0) ?? "",
                    model = parts.ElementAtOrDefault(1) ?? "",
                    taskType = parts.ElementAtOrDefault(2) ?? "",
                    score = double.TryParse(r.Value, out var v) ? v : 0,
                };
            });
            if (source is not null) entries = entries.Where(e => e.source == source);
            if (model is not null) entries = entries.Where(e => e.model == model.ToLowerInvariant());
            return Results.Json(new { entries = entries.OrderBy(e => e.model).ThenBy(e => e.taskType) }, JsonOpts);
        });

        // PUT /api/intelligence/override — user_override fitness (layer 1)
        g.MapPut("/intelligence/override", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var model = b.GetProperty("model").GetString()!;
            var task = b.TryGetProperty("taskType", out var t) ? t.GetString() ?? "default" : "default";
            var score = Math.Clamp(b.GetProperty("score").GetDouble(), 0, 1);
            await ModelIntelligence.SetAsync(db, "user_override", model, task, score);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // DELETE /api/intelligence/override/{model}/{taskType}
        g.MapDelete("/intelligence/override/{model}/{taskType}", async (string model, string taskType, LlmRouterDbContext db) =>
        {
            var row = await db.Kv.FindAsync(ModelIntelligence.Scope,
                $"user_override:{model.ToLowerInvariant()}:{taskType.ToLowerInvariant()}");
            if (row is not null) { db.Kv.Remove(row); await db.SaveChangesAsync(); }
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // POST /api/routing/explain — rank a pool through adaptiveRouting and
        // return per-candidate explanations (reasons + factors), like the
        // upstream decision trace.
        g.MapPost("/routing/explain", async (HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hcf) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var models = b.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Select(e => e.GetString()!).Where(s => s?.Contains('/') == true).ToList()!
                : [];
            var candidates = await AutoRouter.CandidatesAsync(db, models);
            var ranked = AdaptiveRouting.RankCandidates(candidates.Select(c =>
                new AdaptiveRouting.RoutingCandidate(
                    c.Provider, c.Model,
                    CapabilityScore: ModelIntelligence.GetTaskFitnessAsync(db, c.Model, "default")
                        .GetAwaiter().GetResult(),
                    c.CircuitBreakerState == "OPEN" ? AdaptiveRouting.Allocation.Deny
                        : c.CircuitBreakerState == "DEGRADED" ? AdaptiveRouting.Allocation.Warn
                        : AdaptiveRouting.Allocation.Allow,
                    HealthScore: c.CircuitBreakerState switch
                    { "CLOSED" => 1, "DEGRADED" => 0.6, "HALF_OPEN" => 0.4, _ => 0 },
                    c.CircuitBreakerState == "OPEN" ? AdaptiveRouting.Circuit.Open
                        : c.CircuitBreakerState == "HALF_OPEN" ? AdaptiveRouting.Circuit.HalfOpen
                        : AdaptiveRouting.Circuit.Closed,
                    c.QuotaFraction <= 0 ? AdaptiveRouting.QuotaStatus.Exhausted
                        : c.QuotaFraction < 20 ? AdaptiveRouting.QuotaStatus.ApproachingLimit
                        : AdaptiveRouting.QuotaStatus.Healthy,
                    LatencyMs: c.AvgE2ELatencyMs > 0 ? c.AvgE2ELatencyMs : null,
                    ErrorRate: c.ErrorRate,
                    CostPreference: c.CostPer1MTokens > 0 ? 1 - Math.Min(1, c.CostPer1MTokens / 10) : 1)));
            return Results.Json(new
            {
                selected = ranked.Selected,
                candidates = ranked.Candidates,
            }, JsonOpts);
        });
    }
}
