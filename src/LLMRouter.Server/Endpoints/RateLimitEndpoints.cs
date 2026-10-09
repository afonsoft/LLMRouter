using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-039: rateLimits CRUD + live window status.</summary>
public static class RateLimitEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly string[] Scopes = ["apiKey", "provider", "model"];

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/rate-limits", async (LlmRouterDbContext db) =>
            Results.Json(new { rules = await db.RateLimits.OrderBy(r => r.CreatedAt).ToListAsync() }, JsonOpts));

        g.MapPost("/rate-limits", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var scope = Str(b, "scope");
            if (scope is null || !Scopes.Contains(scope))
                return Results.Json(new { error = "scope must be apiKey|provider|model" }, JsonOpts, statusCode: 400);
            var now = Now();
            var rule = new RateLimit
            {
                Id = Guid.NewGuid().ToString("N"),
                Scope = scope,
                ScopeValue = Str(b, "scopeValue") ?? "*",
                Rpm = Int(b, "rpm"),
                Tpm = Int(b, "tpm"),
                Burst = Int(b, "burst"),
                Enabled = !b.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.RateLimits.Add(rule);
            await db.SaveChangesAsync();
            return Results.Json(rule, JsonOpts);
        });

        g.MapPut("/rate-limits/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var rule = await db.RateLimits.FindAsync(id);
            if (rule is null)
                return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (Str(b, "scope") is { } s && Scopes.Contains(s)) rule.Scope = s;
            if (Str(b, "scopeValue") is { } sv) rule.ScopeValue = sv;
            if (b.TryGetProperty("rpm", out var rpm) && rpm.ValueKind == JsonValueKind.Number) rule.Rpm = rpm.GetInt32();
            if (b.TryGetProperty("tpm", out var tpm) && tpm.ValueKind == JsonValueKind.Number) rule.Tpm = tpm.GetInt32();
            if (b.TryGetProperty("burst", out var bu) && bu.ValueKind == JsonValueKind.Number) rule.Burst = bu.GetInt32();
            if (b.TryGetProperty("enabled", out var en)) rule.Enabled = en.ValueKind == JsonValueKind.True;
            rule.UpdatedAt = Now();
            await db.SaveChangesAsync();
            return Results.Json(rule, JsonOpts);
        });

        g.MapDelete("/rate-limits/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var rule = await db.RateLimits.FindAsync(id);
            if (rule is null)
                return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            db.RateLimits.Remove(rule);
            await db.SaveChangesAsync();
            return Results.Json(new { deleted = true }, JsonOpts);
        });

        // live window usage for every rule (the caller's own key hits these too)
        g.MapGet("/rate-limit/status", async (LlmRouterDbContext db, RateLimiter limiter) =>
        {
            var rules = await db.RateLimits.Where(r => r.Enabled).ToListAsync();
            var stats = limiter.Snapshot(rules).ToDictionary(s => s.RuleId);
            return Results.Json(new
            {
                status = rules.Select(r => new
                {
                    r.Id, r.Scope, r.ScopeValue, r.Rpm, r.Tpm, r.Burst,
                    requests = stats.TryGetValue(r.Id, out var st) ? st.Requests : 0,
                    tokens = stats.TryGetValue(r.Id, out var st2) ? st2.Tokens : 0L,
                }),
            }, JsonOpts);
        });
    }

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    private static string? Str(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int Int(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;
}
