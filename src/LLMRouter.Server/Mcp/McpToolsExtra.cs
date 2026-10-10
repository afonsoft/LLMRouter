using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Mcp;

/// <summary>
/// SPEC-094: second batch of canonical MCP tools closing the upstream gap —
/// routing dry-runs, settings setters, pool/cache/db ops, metrics, ccr store,
/// skills/oneproxy/media wrappers. Same naming style as McpTools.
/// </summary>
public static class McpToolsExtra
{
    private static JsonObject Schema(params (string Name, string Type, bool Required)[] props)
    {
        var p = new JsonObject();
        var req = new JsonArray();
        foreach (var (n, t, r) in props)
        {
            p[n] = new JsonObject { ["type"] = t };
            if (r) req.Add(n);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req };
    }

    private sealed record Tool(string Name, string Description, JsonObject InputSchema);

    private static readonly Tool[] Canonical =
    {
        // routing dry-runs / introspection
        new("routing.routeRequest", "Dry-run: resolved provider+combo for a model", Schema(("model", "string", true))),
        new("routing.simulateRoute", "Full candidate ordering for a model", Schema(("model", "string", true))),
        new("routing.bestCombo", "Best combo for a task hint", Schema(("taskType", "string", false))),
        new("routing.pickFastest", "Lowest recent-latency model from usage stats", Schema()),
        new("routing.switchCombo", "Set the active/default combo in settings", Schema(("combo", "string", true))),
        new("routing.testCombo", "Dry-run each model of a combo", Schema(("comboId", "string", true))),
        // setters
        new("settings.setRoutingStrategy", "Update adaptiveRouting strategy", Schema(("strategy", "string", true))),
        new("settings.setResilienceProfile", "Set breaker/cooldown profile preset", Schema(("profile", "string", true))),
        new("settings.setCompressionEngine", "Set default compression engine", Schema(("engine", "string", true))),
        new("settings.setBudgetGuard", "Budget guard config", Schema(("dailyUsd", "number", false), ("monthlyUsd", "number", false), ("warnAt", "number", false))),
        // pool / cache / db
        new("pool.health", "Pool + breaker health summary", Schema()),
        new("pool.sessions", "Sessions of a pool", Schema(("poolId", "string", false))),
        new("pool.reset", "Reset sessions of a pool to idle", Schema(("poolId", "string", true))),
        new("cache.stats", "HotCache counters", Schema()),
        new("cache.flush", "Invalidate all cache entries", Schema()),
        new("db.health", "Sqlite table counts + db size", Schema()),
        new("tool.search", "Fuzzy match over the tool catalog", Schema(("query", "string", true))),
        // metrics
        new("metrics.provider", "Per-provider calls/latency/error snapshot", Schema()),
        new("metrics.combo", "Per-combo usage roll-up", Schema()),
        new("metrics.sessionSnapshot", "Current session/settings snapshot (safe fields)", Schema()),
        new("metrics.costReport", "Spend by provider/model/day", Schema(("from", "string", false), ("to", "string", false))),
        new("quota.check", "Quota pools/windows usage", Schema()),
        new("models.catalog", "Registry+synced model catalog (paged)", Schema(("provider", "string", false), ("page", "number", false))),
        new("pricing.sync", "Trigger pricing sync with an items payload", Schema(("items", "array", false))),
        new("token.sync", "CLI token sync status", Schema()),
        // misc
        new("memory.clear", "Clear the memory store (requires confirm:true)", Schema(("confirm", "boolean", true))),
        new("validation.echo", "Echo args back (connectivity probe)", Schema()),
        new("session.loginTime", "Session/login timestamp", Schema()),
        new("ccr.store", "Store a context-compression blob", Schema(("key", "string", true), ("content", "string", true))),
        new("ccr.retrieve", "Retrieve a ccr blob", Schema(("key", "string", true))),
        new("ccr.list", "List ccr blobs", Schema()),
        new("ccr.stats", "Ccr store stats", Schema()),
        new("ccr.delete", "Delete a ccr blob", Schema(("key", "string", true))),
        new("skills.execute", "Run a skill by id", Schema(("id", "string", true))),
        new("skills.executions", "List skill executions", Schema(("skill", "string", false), ("limit", "number", false))),
        new("oneproxy.current", "Current egress proxy url", Schema()),
        new("oneproxy.rotate", "Rotate egress proxy", Schema()),
        new("rtk.discover", "Context/rtk discovery (stub until SPEC-095)", Schema()),
        new("rtk.learn", "Context/rtk learn (stub until SPEC-095)", Schema()),
        new("media.music", "Loopback /v1/music/generations", Schema(("prompt", "string", true))),
        new("media.video", "Loopback /v1/videos/generations", Schema(("prompt", "string", true))),
        new("web.search", "Loopback /v1/search", Schema(("q", "string", true))),
        new("web.fetch", "Loopback /v1/web/fetch", Schema(("url", "string", true))),
        new("github.skillsSearch", "Skills marketplace search", Schema(("q", "string", false))),
        new("github.skillsInstall", "Install a marketplace skill", Schema(("id", "string", true), ("url", "string", true))),
        new("browser.poolStatus", "Headless browser pool status", Schema()),
    };

    public static IEnumerable<object> ListTools() =>
        Canonical.Select(t => (object)new { name = t.Name, description = t.Description, inputSchema = t.InputSchema });

    public static bool IsCanonical(string name) => Canonical.Any(t => t.Name == name);

    private static string? Arg(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) ? v.ToString() : null;

    /// <summary>Merge-update a section of the settings blob (same path as settings.update).</summary>
    private static async Task SetSettingAsync(LlmRouterDbContext db, string key, JsonNode value, CancellationToken ct)
    {
        var row = await db.Settings.FirstOrDefaultAsync(ct)
            ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        var cur = (JsonNode.Parse(row.Data) ?? new JsonObject()).AsObject();
        cur[key] = value;
        row.Data = cur.ToJsonString();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Loopback POST to a /v1 route carrying the caller's auth headers.</summary>
    private static async Task<object> LoopbackAsync(HttpContext ctx, IHttpClientFactory hf,
        string path, object body)
    {
        var http = hf.CreateClient("loopback");
        var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}{path}";
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
        };
        foreach (var h in new[] { "Authorization", "X-Api-Key", "x-api-key", "x-goog-api-key" })
            if (ctx.Request.Headers.TryGetValue(h, out var v)) req.Headers.TryAddWithoutValidation(h, v.ToString());
        if (ctx.Request.Headers.Cookie.Count > 0)
            req.Headers.TryAddWithoutValidation("Cookie", ctx.Request.Headers.Cookie.ToString());
        var resp = await http.SendAsync(req, ctx.RequestAborted);
        var text = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
        return new { ok = resp.IsSuccessStatusCode, status = (int)resp.StatusCode, body = text[..Math.Min(4000, text.Length)] };
    }

    public static async Task<object?> DispatchAsync(string name, JsonElement args,
        HttpContext ctx, LlmRouterDbContext db, ProviderRegistry registry, IHttpClientFactory hf)
    {
        var ct = ctx.RequestAborted;
        switch (name)
        {
            case "routing.routeRequest":
            case "routing.simulateRoute":
            case "routing.testCombo":
            {
                var model = Arg(args, "model") ?? Arg(args, "comboId") ?? "";
                var engine = ctx.RequestServices.GetRequiredService<GatewayEngine>();
                var targets = await engine.ResolveAsync(model, JsonDocument.Parse("{}").RootElement, ct);
                var list = targets.Select(t => new
                {
                    provider = t.Provider.Id, connection = t.Connection.Id,
                    model = t.UpstreamModel, combo = t.ComboName,
                    breaker = Core.Resilience.ProviderBreaker.GetState(t.Provider.Id).ToString(),
                }).ToList();
                if (name == "routing.routeRequest")
                    return new { model, resolved = list.FirstOrDefault(), candidates = list.Count };
                return new { model, candidates = list };
            }
            case "routing.bestCombo":
            {
                var task = Arg(args, "taskType") ?? "chat";
                var combos = await db.Combos.ToListAsync(ct);
                var scored = combos.Select(c =>
                {
                    var models = JsonSerializer.Deserialize<List<string>>(c.Models) ?? [];
                    var planner = new ComboPlanner(registry);
                    var caps = models.SelectMany(m => planner.GetCapabilitiesForModel(m)).ToHashSet();
                    var score = caps.Count + (c.Name.Contains(task, StringComparison.OrdinalIgnoreCase) ? 10 : 0);
                    return new { c.Id, c.Name, score, caps = caps.OrderBy(x => x) };
                }).OrderByDescending(x => x.score);
                return new { taskType = task, best = scored.FirstOrDefault(), all = scored };
            }
            case "routing.pickFastest":
            {
                var rows = await db.UsageHistory.GroupBy(u => new { u.Provider, u.Model })
                    .Select(g => new { g.Key.Provider, g.Key.Model, avg = g.Average(u => (double)u.LatencyMs), n = g.Count() })
                    .Where(x => x.n >= 1).OrderBy(x => x.avg).Take(10).ToListAsync(ct);
                return new { fastest = rows.FirstOrDefault(), candidates = rows };
            }
            case "routing.switchCombo":
            {
                var combo = Arg(args, "combo");
                if (combo is null) return null;
                var exists = await db.Combos.AnyAsync(c => c.Name == combo || c.Id == combo, ct);
                if (!exists) return new { ok = false, error = $"combo '{combo}' not found" };
                await SetSettingAsync(db, "activeCombo", combo, ct);
                return new { ok = true, activeCombo = combo };
            }
            case "settings.setRoutingStrategy":
            {
                var s = Arg(args, "strategy");
                if (s is null) return null;
                await SetSettingAsync(db, "adaptiveRouting",
                    new JsonObject { ["strategy"] = s, ["enabled"] = true }, ct);
                return new { ok = true, strategy = s };
            }
            case "settings.setResilienceProfile":
            {
                var p = Arg(args, "profile");
                if (p is null) return null;
                await SetSettingAsync(db, "resilienceProfile", p, ct);
                return new { ok = true, profile = p };
            }
            case "settings.setCompressionEngine":
            {
                var e = Arg(args, "engine");
                if (e is null) return null;
                await SetSettingAsync(db, "compressionEngine", e, ct);
                return new { ok = true, engine = e };
            }
            case "settings.setBudgetGuard":
            {
                var guard = new JsonObject();
                foreach (var k in new[] { "dailyUsd", "monthlyUsd", "warnAt" })
                    if (args.TryGetProperty(k, out var v)) guard[k[..1].ToLowerInvariant() + k[1..]] = v.GetRawText() is { } r ? JsonNode.Parse(r) : null;
                await SetSettingAsync(db, "budgetGuard", guard, ct);
                return new { ok = true, budgetGuard = guard };
            }
            case "pool.health":
            {
                var pools = await db.SessionPools.ToListAsync(ct);
                var sessions = await db.PoolSessions.ToListAsync(ct);
                return new
                {
                    pools = pools.Select(p => new
                    {
                        p.Id, p.Provider, p.IsActive,
                        sessions = sessions.Count(s => s.PoolId == p.Id),
                        busy = sessions.Count(s => s.PoolId == p.Id && s.State == "busy"),
                        breaker = Core.Resilience.ProviderBreaker.GetState(p.Provider).ToString(),
                    }),
                };
            }
            case "pool.sessions":
            {
                var pid = Arg(args, "poolId");
                var q = db.PoolSessions.AsQueryable();
                if (pid is not null) q = q.Where(s => s.PoolId == pid);
                var rows = await q.ToListAsync(ct);
                return new { sessions = rows.Select(s => new { s.Id, s.PoolId, s.State, s.Health, s.TotalRequests, s.ConsecutiveFails }) };
            }
            case "pool.reset":
            {
                var pid = Arg(args, "poolId");
                if (pid is null) return null;
                var rows = await db.PoolSessions.Where(s => s.PoolId == pid).ToListAsync(ct);
                foreach (var s in rows) { s.State = "idle"; s.ConsecutiveFails = 0; s.BusyUntil = null; s.CooldownUntil = null; }
                await db.SaveChangesAsync(ct);
                return new { ok = true, reset = rows.Count };
            }
            case "cache.stats":
                return new { hits = HotCache.Default.Hits, misses = HotCache.Default.Misses };
            case "cache.flush":
                HotCache.Default.InvalidateAll();
                return new { ok = true };
            case "db.health":
            {
                var dbPath = db.Database.GetDbConnection().DataSource;
                var size = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;
                var counts = new Dictionary<string, long>
                {
                    ["providers"] = await db.ProviderConnections.LongCountAsync(ct),
                    ["apiKeys"] = await db.ApiKeys.LongCountAsync(ct),
                    ["combos"] = await db.Combos.LongCountAsync(ct),
                    ["usageHistory"] = await db.UsageHistory.LongCountAsync(ct),
                    ["kv"] = await db.Kv.LongCountAsync(ct),
                    ["syncedModels"] = await db.SyncedModels.LongCountAsync(ct),
                };
                return new { ok = true, dbPath, sizeBytes = size, tables = counts };
            }
            case "tool.search":
            {
                var q = (Arg(args, "query") ?? "").ToLowerInvariant();
                var names = McpTools.ListTools().Concat(ListTools())
                    .Select(t => ((dynamic)t).name as string ?? "")
                    .Where(n => n.Contains(q, StringComparison.OrdinalIgnoreCase));
                return new { tools = names };
            }
            case "metrics.provider":
            {
                var rows = await db.UsageHistory.GroupBy(u => u.Provider)
                    .Select(g => new
                    {
                        provider = g.Key, requests = g.LongCount(),
                        errors = g.LongCount(u => u.Status != "ok" && u.Status != "200" && u.Status != null),
                        avgLatency = g.Average(u => (double)u.LatencyMs),
                        cost = g.Sum(u => u.Cost),
                    }).ToListAsync(ct);
                return new { providers = rows };
            }
            case "metrics.combo":
            {
                var combos = await db.Combos.ToListAsync(ct);
                var models = await db.UsageHistory.GroupBy(u => u.Model)
                    .Select(g => new { model = g.Key, requests = g.LongCount() }).ToListAsync(ct);
                return new
                {
                    combos = combos.Select(c => new
                    {
                        c.Id, c.Name,
                        requests = models.Where(m => (JsonSerializer.Deserialize<List<string>>(c.Models) ?? [])
                            .Any(cm => cm.EndsWith('/' + m.model!, StringComparison.Ordinal) || cm == m.model))
                            .Sum(m => m.requests),
                    }),
                };
            }
            case "metrics.sessionSnapshot":
            {
                var row = await db.Settings.FirstOrDefaultAsync(ct);
                var data = row is null ? new JsonObject() : JsonNode.Parse(row.Data)!.AsObject();
                var safe = new JsonObject();
                foreach (var kv in data)
                    if (!kv.Key.Contains("key", StringComparison.OrdinalIgnoreCase)
                        && !kv.Key.Contains("secret", StringComparison.OrdinalIgnoreCase)
                        && !kv.Key.Contains("token", StringComparison.OrdinalIgnoreCase)
                        && !kv.Key.Contains("password", StringComparison.OrdinalIgnoreCase))
                        safe[kv.Key] = kv.Value?.DeepClone();
                return new { settings = safe };
            }
            case "metrics.costReport":
            {
                var rows = await db.UsageHistory.GroupBy(u => new { u.Provider, u.Model })
                    .Select(g => new { g.Key.Provider, g.Key.Model, cost = g.Sum(u => u.Cost), requests = g.LongCount() })
                    .OrderByDescending(x => x.cost).ToListAsync(ct);
                return new { total = rows.Sum(r => r.cost), byModel = rows };
            }
            case "quota.check":
            {
                var windows = await db.QuotaWindows.ToListAsync(ct);
                var pools = await db.Kv.Where(k => k.Scope == "quotaPools" || k.Scope == "quotaGroups").ToListAsync(ct);
                return new
                {
                    windows = windows.Select(w => new { w.Provider, w.Name, w.MaxTokens, w.WindowMinutes }),
                    pools = pools.Select(p => new { p.Scope, p.Key }),
                };
            }
            case "models.catalog":
            {
                var provider = Arg(args, "provider");
                var page = int.TryParse(Arg(args, "page"), out var pg) ? Math.Max(0, pg) : 0;
                var q = db.SyncedModels.AsQueryable();
                if (provider is not null) q = q.Where(s => s.Provider == provider);
                var synced = await q.OrderBy(s => s.Provider).ThenBy(s => s.Model)
                    .Skip(page * 100).Take(100).ToListAsync(ct);
                return new
                {
                    page,
                    models = synced.Select(s => new { s.Provider, s.Model, s.Available, s.MaxOutputTokens }),
                    registry = registry.Providers.Values.SelectMany(p2 => p2.Models.Select(m => $"{p2.Id}/{m.Id}")).Take(200),
                };
            }
            case "pricing.sync":
            {
                var count = 0;
                if (args.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var it in items.EnumerateArray())
                    {
                        var m = it.TryGetProperty("model", out var mv) ? mv.GetString() : null;
                        if (m is null) continue;
                        var row = await db.Kv.FindAsync("pricingOverrides", m);
                        if (row is null) db.Kv.Add(new KvEntry { Scope = "pricingOverrides", Key = m, Value = it.GetRawText() });
                        else row.Value = it.GetRawText();
                        count++;
                    }
                await db.SaveChangesAsync(ct);
                return new { synced = count };
            }
            case "token.sync":
            {
                var rows = await db.Kv.Where(k => k.Scope == "cliTokens" || k.Scope == "tokens").ToListAsync(ct);
                return new { synced = rows.Count, lastSync = rows.Select(r => r.Key) };
            }
            case "memory.clear":
            {
                if (Arg(args, "confirm") is not ("true" or "True")) return new { ok = false, error = "confirm:true required" };
                var rows = await db.Kv.Where(k => k.Scope == "memory").ToListAsync(ct);
                db.Kv.RemoveRange(rows);
                await db.SaveChangesAsync(ct);
                return new { ok = true, cleared = rows.Count };
            }
            case "validation.echo":
                return JsonSerializer.Deserialize<JsonObject>(args.GetRawText()) ?? new JsonObject { ["ok"] = true };
            case "session.loginTime":
                return new { now = DateTimeOffset.UtcNow.ToString("o"), authenticated = true };
            case "ccr.store":
            {
                var key = Arg(args, "key"); var content = Arg(args, "content");
                if (key is null || content is null) return null;
                var row = await db.Kv.FindAsync("ccr", key);
                if (row is null) db.Kv.Add(new KvEntry { Scope = "ccr", Key = key, Value = content });
                else row.Value = content;
                await db.SaveChangesAsync(ct);
                return new { ok = true, key, bytes = content.Length };
            }
            case "ccr.retrieve":
            {
                var row = await db.Kv.FindAsync("ccr", Arg(args, "key") ?? "");
                return row is null ? new { found = false } : (object)new { found = true, content = row.Value };
            }
            case "ccr.list":
            {
                var rows = await db.Kv.Where(k => k.Scope == "ccr").ToListAsync(ct);
                return new { blobs = rows.Select(r => new { r.Key, bytes = r.Value.Length }) };
            }
            case "ccr.stats":
            {
                var rows = await db.Kv.Where(k => k.Scope == "ccr").ToListAsync(ct);
                var runs = await db.CompressionRuns.LongCountAsync(ct);
                return new { blobs = rows.Count, totalBytes = rows.Sum(r => r.Value.Length), compressionRuns = runs };
            }
            case "ccr.delete":
            {
                var row = await db.Kv.FindAsync("ccr", Arg(args, "key") ?? "");
                if (row is null) return new { ok = false, error = "not found" };
                db.Kv.Remove(row);
                await db.SaveChangesAsync(ct);
                return new { ok = true };
            }
            case "skills.execute":
            {
                var id = Arg(args, "id");
                if (id is null) return null;
                var env = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>();
                var (ok, detail) = await Core.Skills.SkillsExtras.RunAsync(db,
                    Path.Combine(env.ContentRootPath, "skills"), id);
                return new { ok, detail };
            }
            case "skills.executions":
            {
                var skill = Arg(args, "skill");
                var limit = int.TryParse(Arg(args, "limit"), out var l) ? Math.Clamp(l, 1, 500) : 50;
                var q = db.SkillExecutions.AsQueryable();
                if (skill is not null) q = q.Where(e => e.Skill == skill);
                return new { executions = await q.OrderByDescending(e => e.Id).Take(limit).ToListAsync(ct) };
            }
            case "oneproxy.current":
            {
                var sdata = await HotReads.SettingsDataAsync(db);
                var cur = await RoutingOps.OneProxyCurrentAsync(db, sdata);
                return new { current = cur };
            }
            case "oneproxy.rotate":
            {
                var sdata = await HotReads.SettingsDataAsync(db);
                var next = await RoutingOps.OneProxyRotateAsync(db, sdata);
                return new { rotated = next };
            }
            case "rtk.discover":
            case "rtk.learn":
            case "browser.poolStatus":
                return new { enabled = false, note = "lands with SPEC-095" };
            case "media.music":
                return await LoopbackAsync(ctx, hf, "/v1/music/generations",
                    new { prompt = Arg(args, "prompt") ?? "" });
            case "media.video":
                return await LoopbackAsync(ctx, hf, "/v1/videos/generations",
                    new { prompt = Arg(args, "prompt") ?? "" });
            case "web.search":
                return await LoopbackAsync(ctx, hf, "/v1/search",
                    new { q = Arg(args, "q") ?? "" });
            case "web.fetch":
                return await LoopbackAsync(ctx, hf, "/v1/web/fetch",
                    new { url = Arg(args, "url") ?? "" });
            case "github.skillsSearch":
            {
                var rows = await Core.Skills.SkillsExtras.MarketplaceAsync(db, hf.CreateClient("upstream"));
                var q = Arg(args, "q") ?? "";
                return new { skills = rows.Where(r => JsonSerializer.Serialize(r).Contains(q, StringComparison.OrdinalIgnoreCase)) };
            }
            case "github.skillsInstall":
            {
                var id = Arg(args, "id"); var url = Arg(args, "url");
                if (id is null || url is null) return null;
                var env = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>();
                var (ok, detail) = await Core.Skills.SkillsExtras.MarketInstallAsync(
                    hf.CreateClient("upstream"), Path.Combine(env.ContentRootPath, "skills"), id, url);
                return new { ok, detail };
            }
            default:
                return null;
        }
    }
}
