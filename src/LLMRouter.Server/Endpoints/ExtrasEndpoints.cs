using System.Net.Http.Json;
using System.Text.Json;
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
                catch { }
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
                        && DateTime.TryParse(r.GetString(), out var dt))
                        cooldownUntil = new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                }
                catch { }
                return new { c.Id, c.Provider, c.IsActive, isFree, cooldownUntil };
            }).Where(x => x.isFree).ToList();
            var healthy = free.Count(x => x.IsActive && (x.cooldownUntil is null || x.cooldownUntil < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            return Results.Json(new { providers = free, total = free.Count, healthy }, JsonOpts);
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

        // ---- memory store (kv 'memory') ----
        g.MapGet("/memory", async (LlmRouterDbContext db, string? q) =>
        {
            var raw = (await db.Kv.FindAsync("memory", "items"))?.Value;
            var arr = raw is null ? JsonDocument.Parse("[]").RootElement
                : JsonDocument.Parse(raw).RootElement;
            // SPEC-026: scored/ranked search (term freq + recency) via MemorySearch
            var items = Core.Extras.MemorySearch.Search(arr, q);
            return Results.Json(new { items }, JsonOpts);
        });
        g.MapPost("/memory", async (LlmRouterDbContext db, HttpContext ctx) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var row = await db.Kv.FindAsync("memory", "items")
                ?? db.Kv.Add(new KvEntry { Scope = "memory", Key = "items", Value = "[]" }).Entity;
            var items = JsonDocument.Parse(row.Value).RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
            if (b.TryGetProperty("id", out var id)) // delete
            {
                items.RemoveAll(x => x.TryGetProperty("id", out var i) && i.GetString() == id.GetString());
                await Core.Extras.KvIndex.RemoveAsync(db, "memory", id.GetString()!);
            }
            else
            {
                var item = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    id = Guid.NewGuid().ToString("N")[..8],
                    at = DateTime.UtcNow,
                    content = b.GetProperty("content").GetString(),
                    tags = b.TryGetProperty("tags", out var t) ? t.GetString() : "",
                })).RootElement.Clone();
                items.Add(item);
                await Core.Extras.KvIndex.UpsertAsync(db, "memory", item.GetProperty("id").GetString()!, item);
            }
            row.Value = JsonSerializer.Serialize(items);
            await db.SaveChangesAsync();
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
        g.MapPost("/batches", async (LlmRouterDbContext db, HttpContext ctx, IServiceScopeFactory scopeFactory) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var items = b.GetProperty("requests").EnumerateArray()
                .Select(x => x.GetRawText()).ToList();
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
                    var http = hf2.CreateClient();
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

        // ---- plugins (bundled skills + mcp servers as installed plugins) ----
        g.MapGet("/plugins", async (LlmRouterDbContext db) =>
        {
            var skillsRaw = (await db.Kv.FindAsync("skills", "disabled"))?.Value;
            var mcpRaw = (await db.Kv.FindAsync("mcpServers", "list"))?.Value;
            return Results.Json(new
            {
                skills = new[] { "token-saver", "combo-builder" },
                disabledSkills = skillsRaw is null ? (object)Array.Empty<object>() : JsonDocument.Parse(skillsRaw).RootElement.Clone(),
                mcpServers = mcpRaw is null ? (object)Array.Empty<object>() : JsonDocument.Parse(mcpRaw).RootElement.Clone(),
            }, JsonOpts);
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

        // ---- audit ----
        g.MapGet("/audit", async (LlmRouterDbContext db) =>
        {
            var raw = (await db.Kv.FindAsync("audit", "log"))?.Value;
            return Results.Json(new { events = raw is null ? (object)Array.Empty<object>() : JsonDocument.Parse(raw).RootElement.Clone() }, JsonOpts);
        });
    }
}
