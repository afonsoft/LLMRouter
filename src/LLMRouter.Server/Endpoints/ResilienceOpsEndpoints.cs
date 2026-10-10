using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

// SPEC-070: model cooldowns, named fallback chains, routing decision traces.
public static class ResilienceOpsEndpoints
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static string Now() => DateTime.UtcNow.ToString("o");

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- model cooldowns (modelCooldowns table; enforced in GatewayPipeline.ResolveAsync) ----
        g.MapGet("/resilience/model-cooldowns", async (LlmRouterDbContext db) =>
            Results.Json(new { cooldowns = await db.ModelCooldowns.OrderByDescending(x => x.Until).ToListAsync() }, JsonOpts));

        g.MapPost("/resilience/model-cooldowns", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (!b.TryGetProperty("model", out var mv) || string.IsNullOrEmpty(mv.GetString()))
                return Results.Json(new { error = "model required" }, JsonOpts, statusCode: 400);
            var model = mv.GetString()!;
            var provider = b.TryGetProperty("provider", out var pv) ? pv.GetString() ?? "" : "";
            var reason = b.TryGetProperty("reason", out var rv) ? rv.GetString() : null;
            string until;
            if (b.TryGetProperty("minutes", out var mm) && mm.ValueKind == JsonValueKind.Number)
                until = DateTime.UtcNow.AddMinutes(mm.GetDouble()).ToString("o");
            else if (b.TryGetProperty("until", out var uv) && uv.ValueKind == JsonValueKind.String)
                until = uv.GetString()!;
            else until = DateTime.UtcNow.AddMinutes(5).ToString("o");
            var row = new ModelCooldown
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Provider = provider, Model = model, Until = until, Reason = reason, CreatedAt = Now(),
            };
            db.ModelCooldowns.Add(row);
            await db.SaveChangesAsync();
            return Results.Json(new { cooldown = row }, JsonOpts);
        });

        g.MapDelete("/resilience/model-cooldowns/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var row = await db.ModelCooldowns.FindAsync(id);
            if (row is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            db.ModelCooldowns.Remove(row);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true }, JsonOpts);
        });

        // ---- fallback chains (fallbackChains table; used when combo candidates exhaust) ----
        g.MapGet("/fallback/chains", async (LlmRouterDbContext db) =>
            Results.Json(new { chains = await db.FallbackChains.ToListAsync() }, JsonOpts));

        g.MapPost("/fallback/chains", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (!b.TryGetProperty("name", out var nv) || string.IsNullOrEmpty(nv.GetString()))
                return Results.Json(new { error = "name required" }, JsonOpts, statusCode: 400);
            var row = new FallbackChain
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = nv.GetString()!,
                Steps = b.TryGetProperty("steps", out var sv) ? sv.GetRawText() : "[]",
                Active = !b.TryGetProperty("active", out var av) || av.GetBoolean(),
                CreatedAt = Now(),
            };
            db.FallbackChains.Add(row);
            await db.SaveChangesAsync();
            return Results.Json(new { chain = row }, JsonOpts);
        });

        g.MapPut("/fallback/chains/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var row = await db.FallbackChains.FindAsync(id);
            if (row is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("name", out var nv)) row.Name = nv.GetString() ?? row.Name;
            if (b.TryGetProperty("steps", out var sv)) row.Steps = sv.GetRawText();
            if (b.TryGetProperty("active", out var av)) row.Active = av.GetBoolean();
            await db.SaveChangesAsync();
            return Results.Json(new { chain = row }, JsonOpts);
        });

        g.MapDelete("/fallback/chains/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var row = await db.FallbackChains.FindAsync(id);
            if (row is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            db.FallbackChains.Remove(row);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true }, JsonOpts);
        });

        // ---- routing decision trace: explainable per-request detail ----
        g.MapGet("/routing/decisions/{requestId}", async (string requestId, LlmRouterDbContext db) =>
        {
            var detail = await db.RequestDetails.FindAsync(requestId);
            if (detail is null)
                return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var model = detail.Model ?? "";
            var usage = await db.UsageHistory
                .Where(u => u.Model == model)
                .OrderByDescending(u => u.Timestamp).Take(10).ToListAsync();
            var trace = JsonDocument.Parse(detail.Data).RootElement.Clone();
            return Results.Json(new { requestId = detail.Id, detail = new
            {
                detail.Timestamp, detail.Provider, detail.Model, detail.ConnectionId, detail.Status,
            }, trace, relatedUsage = usage.Take(5) }, JsonOpts);
        });
    }
}
