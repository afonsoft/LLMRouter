using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Extras;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-015: discovery, free-tiers, gamification/leaderboard, chaos, memory,
/// webhooks, batches, audit.
/// </summary>
public static class ExtrasEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void MapExtrasEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- local provider discovery ----
        g.MapGet("/discovery", async () =>
        {
            var results = new List<object>();
            using var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
            await Task.WhenAll(Extras.DiscoveryTargets.Select(async t =>
            {
                var ok = false;
                try { ok = (await c.GetAsync($"http://127.0.0.1:{t.Port}{t.HealthPath}")).IsSuccessStatusCode; }
                catch { /* best-effort: failure is non-fatal */ }
                lock (results)
                    results.Add(new { name = t.Name, port = t.Port, baseUrl = $"http://127.0.0.1:{t.Port}", detected = ok });
            }));
            return Results.Json(new { providers = results.OrderBy(r => r.GetType().GetProperty("name")!.GetValue(r)) }, JsonOpts);
        });

        // ---- free tiers / free-provider rankings ----
        // providers that are inherently free/local (upstream freeProviderRankings basis)
        string[] LocalFree = ["ollama", "lmstudio", "llamacpp", "vllm", "jan", "localai", "custom", "custom-node"];
        g.MapGet("/free-tiers", async (LlmRouterDbContext db) =>
        {
            var conns = await db.ProviderConnections.ToListAsync();
            var free = conns.Select(c =>
            {
                var isFree = LocalFree.Contains(c.Provider)
                    || (c.Data.Contains("\"free\":true", StringComparison.OrdinalIgnoreCase)
                        || c.Data.Contains("\"freeTier\":true", StringComparison.OrdinalIgnoreCase));
                long? cooldownUntil = null;
                try
                {
                    var d = JsonDocument.Parse(c.Data).RootElement;
                    if (d.TryGetProperty("rateLimitedUntil", out var r)
                        && DateTime.TryParse(r.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
                        cooldownUntil = new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                }
                catch { /* best-effort: failure is non-fatal */ }
                return new { c.Id, c.Provider, c.IsActive, isFree, cooldownUntil };
            }).Where(x => x.isFree).ToList();
            // SPEC-029: rank by real health — breaker state, cooldown, recent
            // latency/errors from usageHistory — not just the free flag.
            var stats = await db.UsageHistory
                .Where(u => free.Select(f => f.Id).Contains(u.ConnectionId ?? ""))
                .GroupBy(u => u.ConnectionId)
                .Select(g => new
                {
                    conn = g.Key,
                    avgLatency = g.Average(u => (double)u.LatencyMs),
                    errors = g.Count(u => u.Status != "ok" && u.Status != "200" && u.Status != null),
                    total = g.Count(),
                }).ToListAsync();
            var ranked = free.Select(f =>
            {
                var st = stats.FirstOrDefault(x => x.conn == f.Id);
                var breaker = Core.Resilience.ProviderBreaker.GetState(f.Provider);
                var cooling = Core.Resilience.CooldownTracker.Remaining(f.Id);
                var errRate = st is { total: > 0 } ? (double)st.errors / st.total : 0;
                var score = 100.0
                    - (breaker.ToString() == "Open" ? 60 : breaker.ToString() == "HalfOpen" ? 30 : 0)
                    - Math.Min(30, cooling.TotalSeconds / 2)
                    - errRate * 30
                    - Math.Min(10, (st?.avgLatency ?? 0) / 2000);
                if (!f.IsActive) score -= 50;
                return new
                {
                    f.Id, f.Provider, f.IsActive, f.isFree, f.cooldownUntil,
                    breaker = breaker.ToString(),
                    cooldownSec = (int)cooling.TotalSeconds,
                    avgLatencyMs = (long)(st?.avgLatency ?? 0),
                    errorRate = Math.Round(errRate, 3),
                    score = Math.Round(score, 1),
                };
            }).OrderByDescending(x => x.score).ToList();
            var healthy = ranked.Count(x => x.IsActive && x.breaker != "Open"
                && (x.cooldownUntil is null || x.cooldownUntil < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            return Results.Json(new { providers = ranked, total = ranked.Count, healthy }, JsonOpts);
        });

        // ---- gamification + leaderboard ----
        g.MapGet("/gamification", async (LlmRouterDbContext db) =>
        {
            var requests = await db.UsageHistory.LongCountAsync();
            var tokens = await db.UsageHistory.SumAsync(r => r.PromptTokens + r.CompletionTokens);
            var providers = await db.ProviderConnections.CountAsync();
            var (xp, level, badges) = Extras.Gamification(requests, tokens, providers);
            return Results.Json(new { xp, level, badges, requests, tokens, providers }, JsonOpts);
        });

        g.MapGet("/leaderboard", async (LlmRouterDbContext db) =>
        {
            var rows = await db.UsageHistory
                .GroupBy(r => r.Model ?? "unknown")
                .Select(x => new { model = x.Key, requests = x.LongCount(), tokens = x.Sum(r => r.PromptTokens + r.CompletionTokens) })
                .OrderByDescending(x => x.requests).Take(20).ToListAsync();
            var byProvider = await db.UsageHistory
                .GroupBy(r => r.Provider ?? "unknown")
                .Select(x => new { provider = x.Key, requests = x.LongCount(), tokens = x.Sum(r => r.PromptTokens + r.CompletionTokens), cost = x.Sum(r => r.Cost) })
                .OrderByDescending(x => x.requests).Take(20).ToListAsync();
            return Results.Json(new { leaderboard = rows, providers = byProvider }, JsonOpts);
        });

        // ---- chaos ----
        g.MapGet("/chaos", async (LlmRouterDbContext db) =>
        {
            var raw = (await db.Kv.FindAsync("chaos", "config"))?.Value;
            return Results.Json(raw is null
                ? new { errorPct = 0, latencyMs = 0 }
                : JsonDocument.Parse(raw).RootElement.Clone(), JsonOpts);
        });
        g.MapPost("/chaos", async (LlmRouterDbContext db, HttpContext ctx) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var row = await db.Kv.FindAsync("chaos", "config")
                ?? db.Kv.Add(new KvEntry { Scope = "chaos", Key = "config", Value = "{}" }).Entity;
            row.Value = JsonSerializer.Serialize(new
            {
                errorPct = b.TryGetProperty("errorPct", out var e) ? e.GetInt32() : 0,
                latencyMs = b.TryGetProperty("latencyMs", out var l) ? l.GetInt32() : 0,
            });
            await db.SaveChangesAsync();
            await Extras.AuditAsync(db, "chaos.update", row.Value);
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- memory store (SPEC-033: pluggable backends kv/obsidian/notion) ----
        g.MapGet("/memory", async (LlmRouterDbContext db, IHttpClientFactory hf, string? q) =>
        {
            var settings = await SettingsEl(db);
            var backend = Core.Extras.MemoryStore.Backend(settings);
            var items = (await Core.Extras.MemoryStore.ListAsync(db, hf, settings))
                .Select(i => JsonSerializer.SerializeToElement(
                    new { id = i.Id, at = i.At, content = i.Content, tags = i.Tags, backend }))
                .ToArray();
            var arr = JsonSerializer.SerializeToElement(items);
            var ranked = Core.Extras.MemorySearch.Search(arr, q);
            return Results.Json(new { items = ranked }, JsonOpts);
        });
        g.MapPost("/memory", async (LlmRouterDbContext db, HttpContext ctx, IHttpClientFactory hf) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var settings = await SettingsEl(db);
            if (b.TryGetProperty("id", out var id)) // delete
            {
                await Core.Extras.MemoryStore.RemoveAsync(db, hf, settings, id.GetString()!);
                await Core.Extras.KvIndex.RemoveAsync(db, "memory", id.GetString()!);
                return Results.Json(new { ok = true }, JsonOpts);
            }
            var item = await Core.Extras.MemoryStore.AddAsync(db, hf, settings,
                b.GetProperty("content").GetString()!,
                b.TryGetProperty("tags", out var t) ? t.GetString() ?? "" : "");
            await Core.Extras.KvIndex.UpsertAsync(db, "memory", item.Id,
                JsonSerializer.SerializeToElement(item));
            return Results.Json(new { ok = true, item }, JsonOpts);
        });

        // ---- memory backend settings (SPEC-033) ----
        g.MapGet("/memory/backend", async (LlmRouterDbContext db) =>
        {
            var s = await SettingsEl(db);
            var backend = Core.Extras.MemoryStore.Backend(s);
            string? vault = null, parent = null; var hasToken = false;
            if (s is { ValueKind: JsonValueKind.Object } o)
            {
                if (o.TryGetProperty("obsidian", out var ob)) vault = ob.TryGetProperty("vaultPath", out var v) ? v.GetString() : null;
                if (o.TryGetProperty("notion", out var n))
                { parent = n.TryGetProperty("parentId", out var p) ? p.GetString() : null;
                  hasToken = n.TryGetProperty("token", out var t) && t.GetString() is { Length: > 0 }; }
            }
            return Results.Json(new { backend, obsidian = new { vaultPath = vault }, notion = new { parentId = parent, hasToken } }, JsonOpts);
        });

        g.MapPost("/memory/backend", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var row = await db.Settings.FindAsync(1)
                ?? db.Settings.Add(new LLMRouter.Core.Data.SettingRow { Id = 1, Data = "{}" }).Entity;
            var data = JsonNode.Parse(row.Data)!.AsObject();
            data["memory"] ??= new JsonObject();
            var mem = data["memory"]!.AsObject();
            if (b.TryGetProperty("backend", out var be)) mem["backend"] = be.GetString();
            if (b.TryGetProperty("obsidian", out var ob))
            {
                data["obsidian"] ??= new JsonObject();
                var obs = data["obsidian"]!.AsObject();
                if (ob.TryGetProperty("vaultPath", out var v)) obs["vaultPath"] = v.GetString();
            }
            if (b.TryGetProperty("notion", out var no))
            {
                data["notion"] ??= new JsonObject();
                var not = data["notion"]!.AsObject();
                if (no.TryGetProperty("token", out var t) && t.GetString() is { Length: > 0 }) not["token"] = t.GetString();
                if (no.TryGetProperty("parentId", out var p)) not["parentId"] = p.GetString();
            }
            row.Data = data.ToJsonString();
            await db.SaveChangesAsync();
            await Extras.AuditAsync(db, "settings.memory-backend", row.Data[..Math.Min(200, row.Data.Length)]);
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- webhooks ----
        g.MapGet("/webhooks", async (LlmRouterDbContext db) =>
        {
            var raw = (await db.Kv.FindAsync("webhooks", "list"))?.Value;
            return Results.Json(new { webhooks = raw is null ? (object)Array.Empty<object>() : JsonDocument.Parse(raw).RootElement.Clone() }, JsonOpts);
        });
        g.MapPost("/webhooks", async (LlmRouterDbContext db, HttpContext ctx) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var row = await db.Kv.FindAsync("webhooks", "list")
                ?? db.Kv.Add(new KvEntry { Scope = "webhooks", Key = "list", Value = "[]" }).Entity;
            var list = JsonDocument.Parse(row.Value).RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
            if (b.TryGetProperty("id", out var id))
            {
                list.RemoveAll(x => x.TryGetProperty("id", out var i) && i.GetString() == id.GetString());
                await Core.Extras.KvIndex.RemoveAsync(db, "webhooks", id.GetString()!);
            }
            else
            {
                var wh = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    id = Guid.NewGuid().ToString("N")[..8],
                    url = b.GetProperty("url").GetString(),
                    events = b.TryGetProperty("events", out var e)
                        ? e.EnumerateArray().Select(x => x.GetString()).ToArray() : new[] { "*" },
                })).RootElement.Clone();
                list.Add(wh);
                await Core.Extras.KvIndex.UpsertAsync(db, "webhooks", wh.GetProperty("id").GetString()!, wh);
            }
            row.Value = JsonSerializer.Serialize(list);
            await db.SaveChangesAsync();
            await Extras.AuditAsync(db, "webhooks.update", "");
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // SPEC-027: rebuild kv item index for a scope (memory|webhooks)
        g.MapPost("/kv/{scope}/reindex", async (string scope, LlmRouterDbContext db) =>
        {
            if (scope is not ("memory" or "webhooks")) return Results.BadRequest(new { error = "unsupported scope" });
            var n = await Core.Extras.KvIndex.ReindexAsync(db, scope);
            await db.SaveChangesAsync();
            return Results.Json(new { scope, indexed = n }, JsonOpts);
        });

        // ---- batches (sequential chat jobs with lifecycle) ----
        g.MapPost("/batches", async (LlmRouterDbContext db, HttpContext ctx, IServiceScopeFactory scopeFactory, IConfiguration cfg) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            List<string> items;
            if (b.TryGetProperty("input_file_id", out var fidEl))
            {
                // SPEC-040: batch input from an uploaded JSONL file (OpenAI shape:
                // each line {custom_id, method, url, body}; the body is posted)
                var fid = fidEl.GetString() ?? "";
                var fe = await db.Files.FindAsync(fid);
                var fpath = fe is null ? null : FileEndpoints.PathFor(cfg, fe.Id);
                if (fpath is null || !File.Exists(fpath))
                    return Results.Json(new { error = "input_file_id not found" }, JsonOpts, statusCode: 400);
                items = File.ReadLines(fpath)
                    .Where(l => l.Trim().Length > 0)
                    .Select(l =>
                    {
                        try
                        {
                            var j = JsonDocument.Parse(l).RootElement;
                            return j.TryGetProperty("body", out var bb) ? bb.GetRawText() : l;
                        }
                        catch { return l; }
                    })
                    .ToList();
            }
            else
            {
                items = b.GetProperty("requests").EnumerateArray()
                    .Select(x => x.GetRawText()).ToList();
            }
            var id = Guid.NewGuid().ToString("N")[..10];
            var auth = ctx.Request.Headers.Authorization.ToString();
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";

            db.Kv.Add(new KvEntry { Scope = "batches", Key = id, Value = JsonSerializer.Serialize(new
            { id, createdAt = DateTime.UtcNow, status = "queued", total = items.Count, done = 0, results = Array.Empty<object>() }) });
            await db.SaveChangesAsync();

            _ = Task.Run(async () =>
            {
                var results = new List<object>();
                try
                {
                    await using var sc = scopeFactory.CreateAsyncScope();
                    var db2 = sc.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
                    var hf2 = sc.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                    var http = hf2.CreateClient("batches");
                    http.Timeout = TimeSpan.FromMinutes(10);
                    http.BaseAddress = new Uri(baseUrl);
                    await UpdateJob(db2, id, "running", items.Count, 0, results);
                    var i = 0;
                    foreach (var item in items)
                    {
                        try
                        {
                            var r = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
                            if (auth != "") r.Headers.TryAddWithoutValidation("Authorization", auth);
                            r.Content = new StringContent(item, System.Text.Encoding.UTF8, "application/json");
                            var resp = await http.SendAsync(r);
                            var body = await resp.Content.ReadAsStringAsync();
                            results.Add(new { index = i, status = (int)resp.StatusCode,
                                body = body[..Math.Min(2000, body.Length)] });
                        }
                        catch (Exception ex) { results.Add(new { index = i, status = 0, error = ex.Message }); }
                        i++;
                        await UpdateJob(db2, id, "running", items.Count, i, results);
                    }
                    await UpdateJob(db2, id, "done", items.Count, items.Count, results);
                }
                catch { /* worker died; job stays at last persisted state */ }
            });
            return Results.Json(new { id, total = items.Count }, JsonOpts);
        });

        static async Task UpdateJob(LlmRouterDbContext db, string id, string status,
            int total, int done, List<object> results)
        {
            var row = await db.Kv.FindAsync("batches", id);
            if (row is null) return;
            row.Value = JsonSerializer.Serialize(new
            { id, status, total, done, results = results.TakeLast(200) });
            await db.SaveChangesAsync();
        }

        g.MapGet("/batches", async (LlmRouterDbContext db) =>
        {
            var rows = await db.Kv.Where(k => k.Scope == "batches").ToListAsync();
            return Results.Json(new
            {
                batches = rows.Select(r => JsonDocument.Parse(r.Value).RootElement.Clone())
                    .OrderByDescending(b => b.TryGetProperty("createdAt", out var c) ? c.GetString() : "")
            }, JsonOpts);
        });

        g.MapGet("/batches/{id}", async (LlmRouterDbContext db, string id) =>
        {
            var row = await db.Kv.FindAsync("batches", id);
            return row is null ? Results.NotFound()
                : Results.Json(JsonDocument.Parse(row.Value).RootElement.Clone(), JsonOpts);
        });

        // ---- plugins (bundled skills + mcp servers + registered plugin hooks) ----
        g.MapGet("/plugins", async (LlmRouterDbContext db) =>
        {
            var skillsRaw = (await db.Kv.FindAsync("skills", "disabled"))?.Value;
            var mcpRaw = (await db.Kv.FindAsync("mcpServers", "list"))?.Value;
            var registered = await PluginHooks.RegisteredAsync(db);
            return Results.Json(new
            {
                skills = new[] { "token-saver", "combo-builder" },
                disabledSkills = skillsRaw is null ? (object)Array.Empty<object>() : JsonDocument.Parse(skillsRaw).RootElement.Clone(),
                mcpServers = mcpRaw is null ? (object)Array.Empty<object>() : JsonDocument.Parse(mcpRaw).RootElement.Clone(),
                registered,
            }, JsonOpts);
        });

        // SPEC-031: plugin registry CRUD — upsert {name,enabled,hooks[]} into kv plugins/registered
        g.MapPost("/plugins", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var r = doc.RootElement;
            var plugins = await PluginHooks.RegisteredAsync(db);
            var id = r.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String
                ? idv.GetString()! : $"plg-{Guid.NewGuid():N}"[..14];
            JsonObject? existing = null;
            foreach (var p in plugins)
                if (p?["id"]?.GetValue<string>() == id) existing = p as JsonObject;
            var plugin = existing ?? new JsonObject { ["id"] = id };
            plugin["name"] = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()! : plugin["name"]?.GetValue<string>() ?? id;
            if (r.TryGetProperty("enabled", out var e))
                plugin["enabled"] = e.ValueKind == JsonValueKind.True;
            else if (plugin["enabled"] is null) plugin["enabled"] = true;
            if (r.TryGetProperty("hooks", out var h) && h.ValueKind == JsonValueKind.Array)
                plugin["hooks"] = JsonNode.Parse(h.GetRawText());
            if (existing is null) plugins.Add(plugin);
            var row = await db.Kv.FindAsync("plugins", "registered");
            if (row is null) db.Kv.Add(new KvEntry { Scope = "plugins", Key = "registered", Value = plugins.ToJsonString() });
            else row.Value = plugins.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, plugin }, JsonOpts);
        });

        g.MapDelete("/plugins/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var plugins = await PluginHooks.RegisteredAsync(db);
            var next = new JsonArray(plugins.Where(p => p?["id"]?.GetValue<string>() != id)
                .Select(p => JsonNode.Parse(p!.ToJsonString())!).ToArray());
            var row = await db.Kv.FindAsync("plugins", "registered");
            if (row is null) db.Kv.Add(new KvEntry { Scope = "plugins", Key = "registered", Value = next.ToJsonString() });
            else row.Value = next.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- search-tools index ----
        g.MapGet("/search-tools", () => Results.Json(new
        {
            tools = new object[]
            {
                new { name = "chat", endpoint = "POST /v1/chat/completions", desc = "Chat completions via combos/providers" },
                new { name = "messages", endpoint = "POST /v1/messages", desc = "Anthropic-format chat" },
                new { name = "responses", endpoint = "POST /v1/responses", desc = "Responses API" },
                new { name = "embeddings", endpoint = "POST /v1/embeddings", desc = "Text embeddings" },
                new { name = "images", endpoint = "POST /v1/images/generations", desc = "Image generation" },
                new { name = "audio.speech", endpoint = "POST /v1/audio/speech", desc = "TTS" },
                new { name = "audio.transcriptions", endpoint = "POST /v1/audio/transcriptions", desc = "STT" },
                new { name = "search", endpoint = "POST /v1/search", desc = "Provider search" },
                new { name = "web.fetch", endpoint = "POST /v1/web/fetch", desc = "URL fetch" },
                new { name = "mcp", endpoint = "POST /mcp", desc = "MCP JSON-RPC server" },
                new { name = "a2a", endpoint = "POST /a2a", desc = "Agent-to-agent tasks" },
            },
        }, JsonOpts));

        // SPEC-031: search-tools registry — kv searchTools/list of {id,kind,url,enabled}
        g.MapGet("/search-tools/registered", async (LlmRouterDbContext db) =>
        {
            var raw = (await db.Kv.FindAsync("searchTools", "list"))?.Value;
            JsonNode tools; try { tools = raw is null ? new JsonArray() : JsonNode.Parse(raw)!; } catch { tools = new JsonArray(); }
            return Results.Json(new { tools }, JsonOpts);
        });

        g.MapPost("/search-tools", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var r = (await JsonDocument.ParseAsync(ctx.Request.Body)).RootElement;
            var raw = (await db.Kv.FindAsync("searchTools", "list"))?.Value;
            var tools = raw is null ? new JsonArray() : JsonNode.Parse(raw)!.AsArray();
            var id = r.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String
                ? idv.GetString()! : $"st-{Guid.NewGuid():N}"[..14];
            JsonObject? tool = tools.FirstOrDefault(t => t?["id"]?.GetValue<string>() == id) as JsonObject;
            tool ??= new JsonObject { ["id"] = id };
            tool["name"] = r.TryGetProperty("name", out var n) ? n.GetString() : tool["name"]?.GetValue<string>() ?? id;
            tool["kind"] = r.TryGetProperty("kind", out var k) ? k.GetString() : tool["kind"]?.GetValue<string>() ?? "webFetch";
            if (r.TryGetProperty("url", out var u)) tool["url"] = u.GetString();
            if (r.TryGetProperty("enabled", out var e)) tool["enabled"] = e.ValueKind == JsonValueKind.True;
            else if (tool["enabled"] is null) tool["enabled"] = true;
            if (!tools.Any(t => t?["id"]?.GetValue<string>() == id)) tools.Add(tool);
            var row = await db.Kv.FindAsync("searchTools", "list");
            if (row is null) db.Kv.Add(new KvEntry { Scope = "searchTools", Key = "list", Value = tools.ToJsonString() });
            else row.Value = tools.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, tool }, JsonOpts);
        });

        g.MapDelete("/search-tools/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var raw = (await db.Kv.FindAsync("searchTools", "list"))?.Value;
            var tools = raw is null ? new JsonArray() : JsonNode.Parse(raw)!.AsArray();
            var next = new JsonArray(tools.Where(t => t?["id"]?.GetValue<string>() != id)
                .Select(t => JsonNode.Parse(t!.ToJsonString())!).ToArray());
            var row = await db.Kv.FindAsync("searchTools", "list");
            if (row is null) db.Kv.Add(new KvEntry { Scope = "searchTools", Key = "list", Value = next.ToJsonString() });
            else row.Value = next.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- audit ----
        g.MapGet("/audit", async (LlmRouterDbContext db) =>
        {
            var raw = (await db.Kv.FindAsync("audit", "log"))?.Value;
            return Results.Json(new { events = raw is null ? (object)Array.Empty<object>() : JsonDocument.Parse(raw).RootElement.Clone() }, JsonOpts);
        });
    }

    /// <summary>settings row 1 .Data parsed as JsonElement ({} when absent).</summary>
    private static async Task<JsonElement> SettingsEl(LlmRouterDbContext db)
    {
        var s = await db.Settings.FindAsync(1);
        return s is null ? JsonDocument.Parse("{}").RootElement
            : JsonDocument.Parse(s.Data).RootElement.Clone();
    }
}
