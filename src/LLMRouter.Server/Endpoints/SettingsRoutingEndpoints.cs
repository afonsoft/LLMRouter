using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Jobs;
using LLMRouter.Core.Routing;
using LLMRouter.Core.Usage;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-047: ip-filter, payload-rules, reasoning-routing (+simulate),
/// task-routing, free-proxies fetch, oneproxy.</summary>
public static class SettingsRoutingEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static async Task<JsonObject> SettingsNode(LlmRouterDbContext db)
    {
        var s = await db.Settings.FirstOrDefaultAsync();
        var d = AuthEndpoints.Parse(s?.Data);
        return d.ValueKind == JsonValueKind.Object ? (JsonNode.Parse(d.GetRawText()) as JsonObject) ?? [] : [];
    }

    private static async Task SaveSection(LlmRouterDbContext db, string key, JsonNode? value)
    {
        var s = await db.Settings.FirstOrDefaultAsync()
            ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        var d = AuthEndpoints.Parse(s.Data).Deserialize<Dictionary<string, JsonElement>>() ?? [];
        d[key] = JsonSerializer.SerializeToElement(value);
        s.Data = JsonSerializer.Serialize(d);
        await db.SaveChangesAsync();
        await Core.Extras.Extras.AuditAsync(db, $"settings.{key}", "update");
    }

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // generic get/put for each routing section
        foreach (var (key, seg) in new[]
        {
            ("ipFilter", "ip-filter"), ("payloadRules", "payload-rules"),
            ("reasoningRoutingRules", "reasoning-routing-rules"),
            ("taskRouting", "task-routing"), ("oneproxy", "oneproxy"),
            ("freeProxies", "free-proxies"),
        })
        {
            g.MapGet($"/settings/{seg}", async (LlmRouterDbContext db) =>
                Results.Json(new { value = (await SettingsNode(db))[key]?.DeepClone() },
                    JsonOpts));

            g.MapPut($"/settings/{seg}", async (HttpContext ctx, LlmRouterDbContext db) =>
            {
                var req = await JsonSerializer.DeserializeAsync<JsonNode>(ctx.Request.Body);
                var val = req is JsonObject o && o.ContainsKey("value") ? o["value"] : req;
                await SaveSection(db, key, val);
                return Results.Json(new { success = true }, JsonOpts);
            });
        }

        // reasoning simulate — returns the matched rule + resulting body without dispatch
        g.MapPost("/settings/reasoning-rules/simulate", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var model = req.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            var body = req.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object
                ? b : JsonSerializer.SerializeToElement(new { model });
            var sdata = await PricingService.SettingsDataAsync(db);
            var (rewritten, pattern, effort) =
                RoutingOps.ApplyReasoningRules(sdata, body, model);
            return Results.Json(new { matched = pattern, effort, body = rewritten }, JsonOpts);
        });

        // task routing resolve
        g.MapPost("/settings/task-routing/resolve", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var task = req.TryGetProperty("taskType", out var t) ? t.GetString() ?? "" : "";
            var sdata = await PricingService.SettingsDataAsync(db);
            var model = RoutingOps.ResolveTaskModel(sdata, task);
            return Results.Json(new { taskType = task, model }, JsonOpts);
        });

        // free proxies — fetch a list source or accept explicit entries, then
        // (re)populate the "free-proxies" pool and health-check it
        g.MapPost("/proxy-pools/fetch-free", async (HttpContext ctx, LlmRouterDbContext db,
            IHttpClientFactory hf) =>
        {
            var req = ctx.Request.ContentLength > 0
                ? await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body)
                : (JsonElement?)null;
            var entries = req is { } r && r.TryGetProperty("entries", out var e)
                && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : null;
            List<string> urls;
            if (entries is { Count: > 0 })
                urls = entries.Select(x => x.StartsWith("http") ? x : $"http://{x}").ToList();
            else
            {
                var source = req is { } r2 && r2.TryGetProperty("source", out var so)
                    ? so.GetString() : null;
                if (string.IsNullOrEmpty(source))
                    return Results.Json(new { error = "provide 'entries' or 'source'" },
                        JsonOpts, statusCode: 400);
                var text = await hf.CreateClient().GetStringAsync(source, ctx.RequestAborted);
                urls = RoutingOps.ParseFreeProxies(text);
            }

            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            var pool = await db.ProxyPools.FirstOrDefaultAsync(p => p.Id == "free-proxies")
                ?? db.ProxyPools.Add(new ProxyPool
                { Id = "free-proxies", CreatedAt = now, UpdatedAt = now, Data = "{}" }).Entity;
            pool.Data = JsonSerializer.Serialize(new
            {
                name = "free-proxies",
                proxies = urls.Select(u => new { url = u, ok = false, failCount = 0 }).ToArray(),
                probeUrl = "https://api.ipify.org/?format=json",
            });
            pool.UpdatedAt = now;
            pool.IsActive = true;
            await db.SaveChangesAsync();
            var (ok, fail) = await BuiltinJobs.ProxyPoolHealthJob.CheckPoolAsync(db, pool, ctx.RequestAborted);
            await db.SaveChangesAsync();
            return Results.Json(new { pool = "free-proxies", count = urls.Count, ok, fail }, JsonOpts);
        });

        // oneproxy current + manual rotate
        g.MapGet("/proxy/oneproxy", async (LlmRouterDbContext db) =>
        {
            var sdata = await PricingService.SettingsDataAsync(db);
            var cur = await RoutingOps.OneProxyCurrentAsync(db, sdata);
            return Results.Json(new { current = cur }, JsonOpts);
        });

        g.MapPost("/proxy/oneproxy/rotate", async (LlmRouterDbContext db) =>
        {
            var sdata = await PricingService.SettingsDataAsync(db);
            var cur = await RoutingOps.OneProxyRotateAsync(db, sdata);
            return Results.Json(new { current = cur }, JsonOpts);
        });
    }
}
