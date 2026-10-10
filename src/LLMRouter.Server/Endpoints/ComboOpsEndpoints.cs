using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

// SPEC-069: combo ops — pool test, persisted ordering, combo-defaults.
public static class ComboOpsEndpoints
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // Dry probe of a combo's candidate pool: per-model connection counts,
        // recent latency/error from usageHistory — no upstream calls.
        g.MapPost("/combos/{id}/test", async (string id, LlmRouterDbContext db) =>
        {
            var c = await db.Combos.FindAsync(id);
            if (c is null)
                return Results.Json(new { error = "combo not found" }, JsonOpts, statusCode: 404);
            var models = JsonSerializer.Deserialize<List<string>>(c.Models) ?? [];
            var conns = await db.ProviderConnections.Where(p => p.IsActive).ToListAsync();
            var modelIds = models.Select(m => m.Contains('/') ? m[(m.IndexOf('/') + 1)..] : m).ToHashSet();
            var recent = await db.UsageHistory
                .Where(u => u.Model != null && modelIds.Contains(u.Model))
                .OrderByDescending(u => u.Timestamp).Take(200).ToListAsync();
            var pool = models.Select(m =>
            {
                var prov = m.Contains('/') ? m[..m.IndexOf('/')] : m;
                var connsFor = conns.Where(x => string.Equals(x.Provider, prov, StringComparison.OrdinalIgnoreCase)).ToList();
                var rows = recent.Where(r => r.Provider + "/" + r.Model == m || r.Model == m).ToList();
                var last = rows.FirstOrDefault();
                return new
                {
                    model = m,
                    provider = prov,
                    connections = connsFor.Count,
                    ok = connsFor.Count > 0,
                    lastStatus = last?.Status,
                    lastLatencyMs = last?.LatencyMs,
                    lastUsed = last?.Timestamp,
                    recentRequests = rows.Count,
                };
            });
            return Results.Json(new { combo = c.Name, ok = pool.All(p => p.ok), pool }, JsonOpts);
        });

        // Persist combo display/fallback order (settings.data.comboOrder = ["id",...]).
        g.MapPost("/combos/reorder", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (!b.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
                return Results.Json(new { error = "ids array required" }, JsonOpts, statusCode: 400);
            var order = ids.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x != "").ToList();
            var s = await db.Settings.FirstOrDefaultAsync();
            if (s is null) { s = new SettingRow { Data = "{}" }; db.Settings.Add(s); }
            var data = JsonNode.Parse(s.Data)!.AsObject();
            data["comboOrder"] = JsonSerializer.SerializeToNode(order);
            s.Data = data.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Json(new { order }, JsonOpts);
        });

        // Combo defaults: which combo serves requests with no/blank model,
        // and the model used for internal "handoff" calls (decompose, judge).
        g.MapGet("/settings/combo-defaults", async (LlmRouterDbContext db) =>
        {
            var s = await db.Settings.FirstOrDefaultAsync();
            var d = JsonNode.Parse(s?.Data ?? "{}")!.AsObject();
            var cd = d["comboDefaults"] as JsonObject;
            return Results.Json(new
            {
                defaultCombo = cd?["defaultCombo"]?.GetValue<string>() ?? "",
                handoffModel = cd?["handoffModel"]?.GetValue<string>() ?? "",
            }, JsonOpts);
        });

        g.MapPut("/settings/combo-defaults", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var s = await db.Settings.FirstOrDefaultAsync();
            if (s is null) { s = new SettingRow { Data = "{}" }; db.Settings.Add(s); }
            var data = JsonNode.Parse(s.Data)!.AsObject();
            var cd = data["comboDefaults"] as JsonObject ?? new JsonObject();
            if (b.TryGetProperty("defaultCombo", out var dc)) cd["defaultCombo"] = dc.GetString() ?? "";
            if (b.TryGetProperty("handoffModel", out var hm)) cd["handoffModel"] = hm.GetString() ?? "";
            data["comboDefaults"] = cd;
            s.Data = data.ToJsonString();
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "settings.combo-defaults", cd.ToJsonString());
            return Results.Json(new { ok = true }, JsonOpts);
        });
    }
}
