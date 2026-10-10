using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-046: purge targets, retention, tier assignment, ops flags.</summary>
public static class SettingsOpsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static readonly string[] PurgeTargets =
        ["call-logs", "detailed-logs", "logs", "quota-snapshots", "request-history", "usage-history"];

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/settings/purge-targets", () =>
            Results.Json(new { targets = PurgeTargets }, JsonOpts));

        g.MapPost("/settings/purge/{target}", async (string target, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var before = ctx.Request.Query["before"].FirstOrDefault();
            var n = await SettingsOps.PurgeAsync(db, target, before);
            if (n < 0) return Results.Json(new { error = $"unknown target '{target}'", targets = PurgeTargets }, JsonOpts, statusCode: 404);
            await Core.Extras.Extras.AuditAsync(db, "settings.purge", $"{target}:{n}");
            return Results.Json(new { target, deleted = n, before = before ?? "now" }, JsonOpts);
        });

        // tier assignment (kv "keytiers")
        g.MapGet("/settings/tiers", async (LlmRouterDbContext db) =>
        {
            var assigns = await db.Kv.Where(k => k.Scope == "keytiers")
                .Select(k => new { key = k.Key, tier = k.Value }).ToListAsync();
            return Results.Json(new { assignments = assigns }, JsonOpts);
        });

        g.MapPut("/settings/tiers/{apiKey}", async (string apiKey, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var tier = b.TryGetProperty("tier", out var t) ? t.GetString() : null;
            var row = await db.Kv.FindAsync("keytiers", apiKey);
            if (tier is null || tier.Length == 0)
            {
                if (row is not null) db.Kv.Remove(row);
            }
            else if (row is null)
                db.Kv.Add(new KvEntry { Scope = "keytiers", Key = apiKey, Value = tier });
            else row.Value = tier;
            await db.SaveChangesAsync();
            return Results.Json(new { apiKey, tier }, JsonOpts);
        });

        // manually re-enable auto-disabled connections now (job also does it on a timer)
        g.MapPost("/settings/reenable-connections", async (LlmRouterDbContext db) =>
        {
            var sdata = await Core.Usage.PricingService.SettingsDataAsync(db);
            var n = await SettingsOps.ReenableDisabledAsync(db, sdata);
            return Results.Json(new { reenabled = n }, JsonOpts);
        });
    }
}
