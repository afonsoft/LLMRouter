using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Registry;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-064: /v1 management &amp; agents surface — token-authed read APIs for
/// external agents (me/status, quotas/check, combos, registered-keys,
/// accounts/limits, classify, explain/routing, auto-combo candidates,
/// issues/report, plugin manifest, compat stubs, management proxies,
/// session-leases). Auth = same gateway key rules as chat (Bearer/x-api-key/?key=,
/// dashboard cookie also accepted).
/// </summary>
public static class V1ExtrasEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Reject when no valid apiKey/cookie; returns the key when authed.</summary>
    private static async Task<string?> KeyOr401(HttpContext ctx, LlmRouterDbContext db)
    {
        var (key, authed) = await GatewayEndpoints.AuthenticatedKey(ctx, db);
        if (!authed) ctx.Response.StatusCode = 401;
        return authed ? key : null;
    }

    /// <summary>Read a numeric field from a keyquota JsonElement (0 when absent).</summary>
    private static long QuotaNum(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static string Mask(string key) =>
        key.Length <= 8 ? new string('*', Math.Max(4, key.Length))
        : $"{key[..4]}…{key[^4..]}";

    public static void Map(WebApplication app)
    {
        foreach (var prefix in new[] { "/v1", "/api/v1" })
        {
            var g = app.MapGroup(prefix);

            // ---- me/status: key identity + quota remaining
            g.MapGet("/me/status", async (HttpContext ctx, LlmRouterDbContext db) =>
            {
                var key = await KeyOr401(ctx, db);
                if (key is null) return;
                var row = await db.ApiKeys.FirstOrDefaultAsync(k => k.Key == key);
                var quota = await Core.Routing.KeyQuota.LimitsAsync(db, key);
                var usedToday = await Core.Routing.KeyQuota.DailyUsedAsync(db, key);
                var daily = QuotaNum(quota, "dailyTokens");
                await ctx.Response.WriteAsJsonAsync(new
                {
                    authenticated = true,
                    keyId = row?.Id,
                    name = row?.Name,
                    masked = Mask(key),
                    restricted = row?.AccessRestricted ?? false,
                    scopes = row?.AccessAllow,
                    quota = new
                    {
                        dailyTokens = daily,
                        usedToday,
                        remaining = daily > 0 ? Math.Max(0, daily - usedToday) : (long?)null,
                    },
                });
            });

            // ---- registered-keys: own keys, masked
            g.MapGet("/registered-keys", async (HttpContext ctx, LlmRouterDbContext db) =>
            {
                var key = await KeyOr401(ctx, db);
                if (key is null) return;
                var rows = await db.ApiKeys.Where(k => k.IsActive).ToListAsync();
                await ctx.Response.WriteAsJsonAsync(new
                {
                    keys = rows.Select(k => new
                    {
                        k.Id, k.Name, masked = Mask(k.Key),
                        own = k.Key == key, k.AccessRestricted,
                    }),
                });
            });

            // ---- accounts/{id}/limits: quota + rate-limit config for a key id
            g.MapGet("/accounts/{id}/limits", async (HttpContext ctx, LlmRouterDbContext db, string id) =>
            {
                if (await KeyOr401(ctx, db) is null) return;
                var row = await db.ApiKeys.FindAsync(id);
                if (row is null) { ctx.Response.StatusCode = 404; return; }
                var quota = await Core.Routing.KeyQuota.LimitsAsync(db, row.Key);
                var limits = await db.RateLimits.Where(l => l.Scope == "apiKey" && l.ScopeValue == row.Key).ToListAsync();
                await ctx.Response.WriteAsJsonAsync(new
                {
                    id, masked = Mask(row.Key),
                    quota = new
                    {
                        dailyTokens = QuotaNum(quota, "dailyTokens"),
                        rpm = QuotaNum(quota, "rpm"),
                        tpm = QuotaNum(quota, "tpm"),
                    },
                    rateLimits = limits.Select(l => new { l.Rpm, l.Tpm, l.Burst, l.Enabled, l.Scope, l.ScopeValue }),
                });
            });

            // ---- quotas/check?model= — would this key+model pass the daily cap?
            g.MapGet("/quotas/check", async (HttpContext ctx, LlmRouterDbContext db) =>
            {
                var key = await KeyOr401(ctx, db);
                if (key is null) return;
                var model = ctx.Request.Query["model"].FirstOrDefault() ?? "";
                var exceeded = await HotReads.DailyCapExceededAsync(db, key, model);
                var quota = await Core.Routing.KeyQuota.LimitsAsync(db, key);
                var usedToday = await Core.Routing.KeyQuota.DailyUsedAsync(db, key);
                var daily2 = QuotaNum(quota, "dailyTokens");
                await ctx.Response.WriteAsJsonAsync(new
                {
                    model, allowed = !exceeded,
                    dailyTokens = daily2, usedToday,
                    remaining = daily2 > 0 ? Math.Max(0, daily2 - usedToday) : (long?)null,
                });
            });

            // ---- combos: list for agents
            g.MapGet("/combos", async (HttpContext ctx, LlmRouterDbContext db) =>
            {
                if (await KeyOr401(ctx, db) is null) return;
                var combos = await db.Combos.ToListAsync();
                await ctx.Response.WriteAsJsonAsync(new
                {
                    combos = combos.Select(c => new
                    {
                        c.Id, c.Name, c.Kind,
                        models = JsonSerializer.Deserialize<List<string>>(c.Models) ?? [],
                    }),
                });
            });

            // ---- classify: prompt → suggested combo (capability heuristic)
            g.MapPost("/classify", async (HttpContext ctx, LlmRouterDbContext db, GatewayEngine engine) =>
            {
                if (await KeyOr401(ctx, db) is null) return;
                var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
                var prompt = b.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
                var combos = await db.Combos.ToListAsync();
                var needsVision = prompt.Contains("image", StringComparison.OrdinalIgnoreCase)
                    || prompt.Contains("picture", StringComparison.OrdinalIgnoreCase);
                var needsCode = prompt.Contains("code", StringComparison.OrdinalIgnoreCase)
                    || prompt.Contains("function", StringComparison.OrdinalIgnoreCase);
                var scored = combos.Select(c =>
                    {
                        var models = JsonSerializer.Deserialize<List<string>>(c.Models) ?? [];
                        var score = 1.0;
                        var name = (c.Name ?? "").ToLowerInvariant();
                        if (needsVision && name.Contains("vision")) score += 2;
                        if (needsCode && (name.Contains("code") || name.Contains("coding"))) score += 2;
                        if (c.Kind == "lkgp") score += 0.5;
                        return (c, score, models.Count);
                    })
                    .OrderByDescending(x => x.score).ThenByDescending(x => x.Item3).ToList();
                var best = scored.FirstOrDefault();
                await ctx.Response.WriteAsJsonAsync(new
                {
                    combo = best.c?.Name,
                    reason = best.c is null ? "no combos configured"
                        : needsVision ? "prompt mentions images → vision combo preferred"
                        : needsCode ? "prompt mentions code → code combo preferred"
                        : "default: highest-scored combo",
                    candidates = scored.Take(5).Select(x => new { x.c.Name, x.score }),
                });
            });

            // ---- explain/routing: dry-run resolution, narrated chain
            g.MapPost("/explain/routing", async (HttpContext ctx, LlmRouterDbContext db, GatewayEngine engine) =>
            {
                if (await KeyOr401(ctx, db) is null) return;
                var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
                var model = b.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                if (model.Length == 0) { ctx.Response.StatusCode = 400; return; }
                var targets = await engine.ResolveAsync(model, b, ctx.RequestAborted);
                var steps = new List<object>();
                steps.Add(new { step = "input", detail = $"model '{model}'" });
                steps.Add(new { step = "resolve", detail = $"{targets.Count} target(s) after alias/combo/strategy/capability pipeline" });
                foreach (var t in targets.Take(8))
                    steps.Add(new
                    {
                        step = "target",
                        detail = $"{t.Provider.Id} via '{t.Connection.Name}' → upstream '{t.UpstreamModel}'"
                            + (t.ComboName is not null ? $" (combo {t.ComboName})" : ""),
                    });
                await ctx.Response.WriteAsJsonAsync(new { model, resolved = targets.Count > 0, chain = steps });
            });

            // ---- auto-combo/{channel}/candidates: pool for a channel
            g.MapGet("/auto-combo/{channel}/candidates", async (HttpContext ctx, LlmRouterDbContext db, GatewayEngine engine, string channel) =>
            {
                if (await KeyOr401(ctx, db) is null) return;
                var targets = await engine.ResolveAsync($"auto/{channel}", null, ctx.RequestAborted);
                await ctx.Response.WriteAsJsonAsync(new
                {
                    channel,
                    candidates = targets.Select(t => new
                    {
                        provider = t.Provider.Id, connection = t.Connection.Id,
                        t.Connection.Name, upstreamModel = t.UpstreamModel,
                    }),
                });
            });

            // ---- issues/report: client-reported error → audit event
            g.MapPost("/issues/report", async (HttpContext ctx, LlmRouterDbContext db) =>
            {
                var key = await KeyOr401(ctx, db);
                if (key is null) return;
                var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
                var msg = b.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                var id = Guid.NewGuid().ToString("N")[..12];
                await Core.Extras.Extras.AuditAsync(db, "issue.report",
                    $"{id}: {msg[..Math.Min(500, msg.Length)]} (key {Mask(key)})");
                await ctx.Response.WriteAsJsonAsync(new { ok = true, id });
            });

            // ---- provider-plugin-manifest
            g.MapGet("/provider-plugin-manifest", async (HttpContext ctx, LlmRouterDbContext db) =>
            {
                if (await KeyOr401(ctx, db) is null) return;
                var plugins = await HotReads.RegisteredPluginsAsync(db);
                await ctx.Response.WriteAsJsonAsync(new { plugins });
            });

            // ---- muse-code/models: code-capable models from the registry
            g.MapGet("/muse-code/models", async (HttpContext ctx, LlmRouterDbContext db, ProviderRegistry r) =>
            {
                if (await KeyOr401(ctx, db) is null) return;
                var models = r.Providers.Values
                    .SelectMany(p => (p.Models ?? []).Select(m => $"{p.Id}/{m.Id}"))
                    .Where(x => x.Contains("codex", StringComparison.OrdinalIgnoreCase)
                        || x.Contains("code", StringComparison.OrdinalIgnoreCase)
                        || x.Contains("gpt", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                await ctx.Response.WriteAsJsonAsync(new { models });
            });

            // ---- video-bridge/drilldown: video-kind connections + usage
            g.MapGet("/video-bridge/drilldown", async (HttpContext ctx, LlmRouterDbContext db) =>
            {
                if (await KeyOr401(ctx, db) is null) return;
                var conns = (await db.ProviderConnections.Where(c => c.IsActive).ToListAsync())
                    .Where(c => Core.Routing.MediaKinds.KindsOf(c).Contains("video"))
                    .Select(c => new { c.Id, c.Provider, c.Name })
                    .ToList();
                await ctx.Response.WriteAsJsonAsync(new { connections = conns });
            });

            // ---- antigravity: compat stub (upstream IDE bridge — not implemented)
            g.MapGet("/antigravity", async (HttpContext ctx, LlmRouterDbContext db) =>
            {
                if (await KeyOr401(ctx, db) is null) return;
                await ctx.Response.WriteAsJsonAsync(new { enabled = false, providers = Array.Empty<string>() });
            });

            // ---- session-leases: active chat sessions for this key
            g.MapGet("/session-leases", async (HttpContext ctx, LlmRouterDbContext db) =>
            {
                var key = await KeyOr401(ctx, db);
                if (key is null) return;
                var keyRow = await db.ApiKeys.FirstOrDefaultAsync(k => k.Key == key);
                var leases = keyRow is null ? [] : await db.ChatSessions
                    .Where(s => s.KeyId == keyRow.Id)
                    .OrderByDescending(s => s.LastSeenAt).Take(50).ToListAsync();
                await ctx.Response.WriteAsJsonAsync(new
                {
                    leases = leases.Select(s => new { s.Id, s.Model, s.StartedAt, s.LastSeenAt, s.MessageCount }),
                });
            });

            // ---- management/proxies(+subscriptions): mirror of /api proxy pools
            g.MapGet("/management/proxies", async (HttpContext ctx, LlmRouterDbContext db) =>
            {
                if (await KeyOr401(ctx, db) is null) return;
                var pools = await db.ProxyPools.ToListAsync();
                await ctx.Response.WriteAsJsonAsync(new
                {
                    proxies = pools.Select(p => new { p.Id, p.IsActive, p.TestStatus }),
                });
            });
            g.MapPost("/management/proxies", async (HttpContext ctx, LlmRouterDbContext db) =>
            {
                if (await KeyOr401(ctx, db) is null) return;
                var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
                var pool = new ProxyPool
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    IsActive = true,
                    Data = b.GetRawText(),
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                };
                db.ProxyPools.Add(pool);
                await db.SaveChangesAsync();
                await ctx.Response.WriteAsJsonAsync(new { pool.Id });
            });
            g.MapDelete("/management/proxies/{id}", async (HttpContext ctx, LlmRouterDbContext db, string id) =>
            {
                if (await KeyOr401(ctx, db) is null) return;
                var pool = await db.ProxyPools.FindAsync(id);
                if (pool is null) { ctx.Response.StatusCode = 404; return; }
                db.ProxyPools.Remove(pool);
                await db.SaveChangesAsync();
                await ctx.Response.WriteAsJsonAsync(new { deleted = true });
            });
            g.MapGet("/management/proxy-subscriptions", async (HttpContext ctx, LlmRouterDbContext db) =>
            {
                if (await KeyOr401(ctx, db) is null) return;
                var rows = await db.Kv.Where(k => k.Scope == "proxySubscriptions").ToListAsync();
                await ctx.Response.WriteAsJsonAsync(new
                {
                    subscriptions = rows.Select(k => new { k.Key, data = k.Value }),
                });
            });
        }
    }
}
