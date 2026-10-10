using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-041: advanced keys + quota. Key groups, key lifecycle actions
/// (regenerate/reveal), per-key usage limits (rpm/tpm enforced through
/// SPEC-039 rules; dailyTokens through kv counters), quota plans and
/// windowed reset schedules.
/// </summary>
public static class KeysQuotaEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const string QuotaScope = KeyQuota.QuotaScope;

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- key groups ----
        g.MapGet("/keys/groups", async (LlmRouterDbContext db) =>
            Results.Json(new { groups = await db.KeyGroups.ToListAsync() }, JsonOpts));

        g.MapPost("/keys/groups", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var grp = new KeyGroup
            {
                Id = Guid.NewGuid().ToString("N")[..10],
                Name = b.TryGetProperty("name", out var n) ? n.GetString() ?? "group" : "group",
                KeysJson = b.TryGetProperty("keys", out var k) ? k.GetRawText() : "[]",
                CreatedAt = Now(),
            };
            db.KeyGroups.Add(grp);
            await db.SaveChangesAsync();
            return Results.Json(new { group = grp }, JsonOpts);
        });

        g.MapPut("/keys/groups/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var grp = await db.KeyGroups.FindAsync(id);
            if (grp is null) return NotFound("group");
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("name", out var n)) grp.Name = n.GetString() ?? grp.Name;
            if (b.TryGetProperty("keys", out var k)) grp.KeysJson = k.GetRawText();
            await db.SaveChangesAsync();
            return Results.Json(new { group = grp }, JsonOpts);
        });

        g.MapDelete("/keys/groups/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var grp = await db.KeyGroups.FindAsync(id);
            if (grp is null) return NotFound("group");
            db.KeyGroups.Remove(grp);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true }, JsonOpts);
        });

        // ---- key lifecycle actions ----
        g.MapPost("/keys/{id}/regenerate", async (string id, LlmRouterDbContext db) =>
        {
            var k = await db.ApiKeys.FindAsync(id);
            if (k is null) return NotFound("key");
            var old = k.Key;
            k.Key = $"sk-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
            await db.SaveChangesAsync();
            // move kv-scoped settings + SPEC-039 rules to the new key string
            var limits = await db.Kv.FindAsync(QuotaScope, old);
            if (limits is not null)
            {
                db.Kv.Remove(limits);
                db.Kv.Add(new KvEntry { Scope = QuotaScope, Key = k.Key, Value = limits.Value });
            }
            foreach (var r in await db.RateLimits.Where(r => r.Scope == "apiKey" && r.ScopeValue == old).ToListAsync())
                r.ScopeValue = k.Key;
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "apikey.regenerate", k.Name ?? k.Id);
            return Results.Json(new { key = k }, JsonOpts);
        });

        g.MapPost("/keys/{id}/reveal", async (string id, LlmRouterDbContext db) =>
        {
            var k = await db.ApiKeys.FindAsync(id);
            if (k is null) return NotFound("key");
            await Core.Extras.Extras.AuditAsync(db, "apikey.reveal", k.Name ?? k.Id);
            return Results.Json(new { key = k.Key }, JsonOpts);
        });

        // ---- per-key usage limits ----
        g.MapGet("/keys/{id}/usage-limits", async (string id, LlmRouterDbContext db) =>
        {
            var k = await db.ApiKeys.FindAsync(id);
            if (k is null) return NotFound("key");
            return Results.Json(new
            {
                keyId = k.Id,
                limits = await KeyQuota.LimitsAsync(db, k.Key),
                dailyUsed = await KeyQuota.DailyUsedAsync(db, k.Key),
                modelUsage = await KeyQuota.ModelUsageAsync(db, k.Key),
            }, JsonOpts);
        });

        g.MapPut("/keys/{id}/usage-limits", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var k = await db.ApiKeys.FindAsync(id);
            if (k is null) return NotFound("key");
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var doc = new Dictionary<string, JsonElement>();
            if (b.TryGetProperty("rpm", out var r) && r.TryGetInt32(out var rpm) && rpm > 0) doc["rpm"] = r.Clone();
            if (b.TryGetProperty("tpm", out var t) && t.TryGetInt32(out var tpm) && tpm > 0) doc["tpm"] = t.Clone();
            if (b.TryGetProperty("dailyTokens", out var d) && d.TryGetInt64(out var dt) && dt > 0)
                doc["dailyTokens"] = JsonSerializer.SerializeToElement((int)Math.Min(dt, int.MaxValue));
            // SPEC-083: optional `models` glob list scopes the daily cap (qtSd/)
            if (b.TryGetProperty("models", out var mdls) && mdls.ValueKind == JsonValueKind.Array)
                doc["models"] = mdls.Clone();
            var row = await db.Kv.FindAsync(QuotaScope, k.Key);
            if (row is null) db.Kv.Add(new KvEntry { Scope = QuotaScope, Key = k.Key, Value = JsonSerializer.Serialize(doc) });
            else row.Value = JsonSerializer.Serialize(doc);

            // enforce via SPEC-039 rules: replace all apiKey-scope rules for this
            // key with ones derived from the limits (rpm -> Rpm, tpm -> Tpm)
            db.RateLimits.RemoveRange(db.RateLimits.Where(x => x.Scope == "apiKey" && x.ScopeValue == k.Key));
            if (doc.TryGetValue("rpm", out var r2))
                db.RateLimits.Add(new RateLimit { Id = Guid.NewGuid().ToString("N")[..12], Scope = "apiKey", ScopeValue = k.Key, Rpm = r2.GetInt32(), Enabled = true, CreatedAt = Now(), UpdatedAt = Now() });
            if (doc.TryGetValue("tpm", out var t2))
                db.RateLimits.Add(new RateLimit { Id = Guid.NewGuid().ToString("N")[..12], Scope = "apiKey", ScopeValue = k.Key, Tpm = t2.GetInt32(), Enabled = true, CreatedAt = Now(), UpdatedAt = Now() });
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "apikey.usage_limits", k.Name ?? k.Id);
            return Results.Json(new { limits = doc }, JsonOpts);
        });

        // ---- quota plans ----
        g.MapGet("/quota/plans", async (LlmRouterDbContext db) =>
            Results.Json(new { plans = await db.QuotaPlans.ToListAsync() }, JsonOpts));

        g.MapPost("/quota/plans", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var p = new QuotaPlan
            {
                Id = Guid.NewGuid().ToString("N")[..10],
                Name = b.TryGetProperty("name", out var n) ? n.GetString() ?? "plan" : "plan",
                LimitsJson = b.TryGetProperty("limits", out var l) ? l.GetRawText() : "{}",
                Price = b.TryGetProperty("price", out var pr) && pr.TryGetDouble(out var d) ? d : null,
                CreatedAt = Now(),
            };
            db.QuotaPlans.Add(p);
            await db.SaveChangesAsync();
            return Results.Json(new { plan = p }, JsonOpts);
        });

        g.MapPut("/quota/plans/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var p = await db.QuotaPlans.FindAsync(id);
            if (p is null) return NotFound("plan");
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("name", out var n)) p.Name = n.GetString() ?? p.Name;
            if (b.TryGetProperty("limits", out var l)) p.LimitsJson = l.GetRawText();
            if (b.TryGetProperty("price", out var pr)) p.Price = pr.ValueKind == JsonValueKind.Null ? null : pr.GetDouble();
            await db.SaveChangesAsync();
            return Results.Json(new { plan = p }, JsonOpts);
        });

        g.MapDelete("/quota/plans/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var p = await db.QuotaPlans.FindAsync(id);
            if (p is null) return NotFound("plan");
            db.QuotaPlans.Remove(p);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true }, JsonOpts);
        });

        // assign a plan to a key = copy plan limits into the key's usage-limits
        g.MapPost("/keys/{id}/plan", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var k = await db.ApiKeys.FindAsync(id);
            if (k is null) return NotFound("key");
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var p = await db.QuotaPlans.FindAsync(b.TryGetProperty("planId", out var pi) ? pi.GetString() : "");
            if (p is null) return NotFound("plan");
            var limits = JsonDocument.Parse(p.LimitsJson).RootElement;
            var row = await db.Kv.FindAsync(QuotaScope, k.Key);
            if (row is null) db.Kv.Add(new KvEntry { Scope = QuotaScope, Key = k.Key, Value = limits.GetRawText() });
            else row.Value = limits.GetRawText();
            db.RateLimits.RemoveRange(db.RateLimits.Where(x => x.Scope == "apiKey" && x.ScopeValue == k.Key));
            if (limits.TryGetProperty("rpm", out var r) && r.TryGetInt32(out var rpm) && rpm > 0)
                db.RateLimits.Add(new RateLimit { Id = Guid.NewGuid().ToString("N")[..12], Scope = "apiKey", ScopeValue = k.Key, Rpm = rpm, Enabled = true, CreatedAt = Now(), UpdatedAt = Now() });
            if (limits.TryGetProperty("tpm", out var t) && t.TryGetInt32(out var tpm) && tpm > 0)
                db.RateLimits.Add(new RateLimit { Id = Guid.NewGuid().ToString("N")[..12], Scope = "apiKey", ScopeValue = k.Key, Tpm = tpm, Enabled = true, CreatedAt = Now(), UpdatedAt = Now() });
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "apikey.plan_assign", $"{k.Name ?? k.Id} -> {p.Name}");
            return Results.Json(new { key = k, plan = p }, JsonOpts);
        });

        // ---- quota preview: dry-run a request against limits ----
        g.MapPost("/quota/preview", async (HttpContext ctx, LlmRouterDbContext db, RateLimiter limiter) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var model = b.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            var estTokens = b.TryGetProperty("estTokens", out var et) && et.TryGetInt32(out var e) ? e : 0;
            var keyStr = b.TryGetProperty("key", out var ks) ? ks.GetString() : null;
            var keyId = b.TryGetProperty("keyId", out var ki) ? ki.GetString() : null;

            var reasons = new List<string>();
            ApiKey? keyRow = null;
            if (keyStr is not null) keyRow = await db.ApiKeys.FirstOrDefaultAsync(x => x.Key == keyStr);
            else if (keyId is not null) keyRow = await db.ApiKeys.FindAsync(keyId);
            if (keyRow is null) reasons.Add("key not found");
            else if (!keyRow.IsActive) reasons.Add("key disabled");

            long dailyUsed = 0; int? dailyCap = null;
            var apiKeyStr = keyRow?.Key ?? "dashboard";
            if (keyRow is not null)
            {
                var limits = await KeyQuota.LimitsAsync(db, apiKeyStr);
                dailyUsed = await KeyQuota.DailyUsedAsync(db, apiKeyStr);
                if (limits.TryGetProperty("dailyTokens", out var dc) && dc.TryGetInt32(out var cap))
                {
                    dailyCap = cap;
                    if (dailyUsed + estTokens > cap) reasons.Add($"daily token cap {cap} (used {dailyUsed})");
                }
                var rules = await db.RateLimits.Where(x => x.Enabled).ToListAsync();
                if (limiter.Peek(rules, apiKeyStr, null, model) is { } v)
                    reasons.Add($"rate limited: {v.Scope} {v.Kind} {v.Limit}/min (retry in {v.RetryAfterSec}s)");
            }
            return Results.Json(new { allow = reasons.Count == 0, reasons, dailyUsed, dailyCap }, JsonOpts);
        });

        // ---- pool usage/log (pools = provider connections grouped view) ----
        g.MapGet("/quota/pools/{id}/usage", async (string id, LlmRouterDbContext db) =>
        {
            var cutoff = DateTime.UtcNow.AddHours(-24).ToString("yyyy-MM-dd HH:mm:ss");
            var rows = await db.UsageHistory
                .Where(r => r.ConnectionId == id && string.Compare(r.Timestamp, cutoff) > 0).ToListAsync();
            return Results.Json(new
            {
                pool = id, windowHours = 24,
                requests = rows.Count, errors = rows.Count(r => r.Status != "ok"),
                promptTokens = rows.Sum(r => r.PromptTokens),
                completionTokens = rows.Sum(r => r.CompletionTokens),
            }, JsonOpts);
        });

        g.MapGet("/quota/pools/{id}/log", async (string id, int? limit, LlmRouterDbContext db) =>
        {
            var rows = await db.UsageHistory.Where(r => r.ConnectionId == id)
                .OrderByDescending(r => r.Timestamp).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync();
            return Results.Json(new { log = rows }, JsonOpts);
        });

        // ---- quota reset schedules ----
        g.MapGet("/quota/schedules", async (LlmRouterDbContext db) =>
            Results.Json(new { schedules = await db.QuotaSchedules.ToListAsync() }, JsonOpts));

        g.MapPost("/quota/schedules", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var s = new QuotaSchedule
            {
                Id = Guid.NewGuid().ToString("N")[..10],
                Target = b.TryGetProperty("target", out var t) ? t.GetString() ?? "all" : "all",
                Window = b.TryGetProperty("window", out var w) ? w.GetString() ?? "daily" : "daily",
                CreatedAt = Now(),
            };
            db.QuotaSchedules.Add(s);
            await db.SaveChangesAsync();
            return Results.Json(new { schedule = s }, JsonOpts);
        });

        g.MapDelete("/quota/schedules/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var s = await db.QuotaSchedules.FindAsync(id);
            if (s is null) return NotFound("schedule");
            db.QuotaSchedules.Remove(s);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true }, JsonOpts);
        });
    }

    private static IResult NotFound(string what) =>
        Results.Json(new { error = $"{what} not found" }, JsonOpts, statusCode: 404);
}
