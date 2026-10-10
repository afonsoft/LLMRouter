using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Mcp;

/// <summary>
/// SPEC-019: canonical MCP tool surface (paridade com open-sse/mcp-server do upstream).
/// Tools operate directly on the db/registry — same data as the management API.
/// </summary>
public static class McpTools
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
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = p,
            ["required"] = req,
        };
    }

    private sealed record Tool(string Name, string Description, JsonObject InputSchema);

    private static readonly Tool[] Canonical =
    {
        new("providers.list", "List all registered LLM providers", Schema()),
        new("providers.get", "Get provider details", Schema(("id", "string", true))),
        new("providers.test", "Probe connectivity to a provider's base URL", Schema(("id", "string", true))),
        new("models.list", "List models exposed by the gateway (combos + aliases)", Schema()),
        new("connections.list", "List provider connections", Schema(("provider", "string", false))),
        new("connections.create", "Create a provider connection", Schema(("provider", "string", true), ("apiKey", "string", false), ("name", "string", false))),
        new("apiKeys.list", "List gateway API keys (masked)", Schema()),
        new("apiKeys.create", "Create a gateway API key", Schema(("name", "string", false))),
        new("apiKeys.revoke", "Deactivate a gateway API key", Schema(("id", "string", true))),
        new("combos.list", "List combos", Schema()),
        new("combos.create", "Create a combo", Schema(("name", "string", true), ("models", "array", true), ("kind", "string", false))),
        new("combos.run", "Run a chat completion through a combo/model", Schema(("name", "string", true), ("prompt", "string", true))),
        new("usage.stats", "Aggregated usage stats", Schema(("range", "string", false))),
        new("usage.timeseries", "Daily usage timeseries", Schema(("range", "string", false))),
        new("logs.list", "Recent request logs", Schema(("limit", "number", false))),
        new("settings.get", "Get router settings", Schema()),
        new("settings.update", "Merge-update router settings", Schema(("data", "object", true))),
        new("skills.list", "List skills (bundled + registered)", Schema()),
        new("memory.search", "Search memory items", Schema(("q", "string", false))),
        new("memory.add", "Add a memory item", Schema(("content", "string", true))),
        new("pools.list", "List proxy pools", Schema()),
        new("token-health.list", "Connection/token health status", Schema()),
        new("docs.search", "Search built-in docs index", Schema(("q", "string", true))),
        new("system.status", "Router status: version, counts, uptime", Schema()),
        new("chat", "Chat completion via any configured model/combo", Schema(("model", "string", true), ("prompt", "string", true))),
        new("gamification.get", "XP, level and badges for this router", Schema()),
        new("plugins.list", "List registered plugins", Schema()),
        new("plugins.toggle", "Enable/disable a plugin", Schema(("id", "string", true), ("enabled", "boolean", true))),
        new("searchTools.list", "Web search/fetch tool connections", Schema()),
        new("searchTools.run", "Run a registered search tool (webFetch fetches url, search proxies a connection)", Schema(("id", "string", true), ("url", "string", false), ("q", "string", false))),
        new("localCorpus.search", "Search local corpus (stub: returns empty index)", Schema(("q", "string", true))),
    };

    public static IEnumerable<object> ListTools() =>
        Canonical.Select(t => (object)new { name = t.Name, description = t.Description, inputSchema = t.InputSchema });

    public static bool IsCanonical(string name) => Canonical.Any(t => t.Name == name);

    private static string? Arg(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) ? v.ToString() : null;

    public static async Task<object?> DispatchAsync(string name, JsonElement args,
        HttpContext ctx, LlmRouterDbContext db, ProviderRegistry registry, IHttpClientFactory hf,
        Func<string, string, Task<string>> chat)
    {
        switch (name)
        {
            case "providers.list":
                return new
                {
                    providers = registry.UiProviders().Select(u => new
                    { id = u.Entry.Id, name = u.Entry.Name, category = u.Category }),
                };
            case "providers.get":
            {
                var p = registry.GetProvider(Arg(args, "id") ?? "");
                return p is null ? null : (object)new { id = p.Id, p.AuthType, baseUrl = p.BaseUrl };
            }
            case "providers.test":
            {
                var p = registry.GetProvider(Arg(args, "id") ?? "");
                if (p is null) return null;
                var c = hf.CreateClient("upstream");
                c.Timeout = TimeSpan.FromSeconds(5);
                try
                {
                    var url = registry.GetModelsUrl(p) ?? p.BaseUrl;
                    using var resp = await c.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ctx.RequestAborted);
                    return new { id = p.Id, reachable = true, status = (int)resp.StatusCode };
                }
                catch (Exception ex) { return new { id = p.Id, reachable = false, error = ex.Message }; }
            }
            case "models.list":
            {
                var combos = await db.Combos.Select(c => c.Name).ToListAsync(ctx.RequestAborted);
                var aliases = await db.Kv.Where(k => k.Scope == "modelAliases").ToListAsync(ctx.RequestAborted);
                return new { models = combos, aliases = aliases.Select(a => a.Key) };
            }
            case "connections.list":
            {
                var q = db.ProviderConnections.AsQueryable();
                if (Arg(args, "provider") is { } prov) q = q.Where(c => c.Provider == prov);
                var rows = await q.ToListAsync(ctx.RequestAborted);
                return new
                {
                    connections = rows.Select(c => new
                    { c.Id, c.Provider, c.IsActive, c.Priority, c.UpdatedAt }),
                };
            }
            case "connections.create":
            {
                var provider = Arg(args, "provider");
                if (provider is null) return null;
                var now = DateTimeOffset.UtcNow.ToString("o");
                var e = db.ProviderConnections.Add(new ProviderConnection
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Provider = provider,
                    AuthType = "apikey",
                    Name = Arg(args, "name"),
                    IsActive = true,
                    Data = JsonSerializer.Serialize(new { apiKey = Arg(args, "apiKey") }),
                    CreatedAt = now,
                    UpdatedAt = now,
                }).Entity;
                await db.SaveChangesAsync(ctx.RequestAborted);
                return new { e.Id, e.Provider };
            }
            case "apiKeys.list":
            {
                var keys = await db.ApiKeys.ToListAsync(ctx.RequestAborted);
                return new
                {
                    keys = keys.Select(k => new
                    {
                        k.Id, k.Name, k.IsActive, k.CreatedAt,
                        key = k.Key.Length > 8 ? k.Key[..4] + "…" + k.Key[^4..] : "***",
                    }),
                };
            }
            case "apiKeys.create":
            {
                var key = "sk-llmr-" + Convert.ToHexString(Guid.NewGuid().ToByteArray())[..32].ToLowerInvariant();
                var e = db.ApiKeys.Add(new ApiKey
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Key = key,
                    Name = Arg(args, "name") ?? "mcp-key",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
                }).Entity;
                await db.SaveChangesAsync(ctx.RequestAborted);
                return new { e.Id, key };
            }
            case "apiKeys.revoke":
            {
                var k = await db.ApiKeys.FindAsync(Arg(args, "id"));
                if (k is null) return null;
                k.IsActive = false;
                await db.SaveChangesAsync(ctx.RequestAborted);
                return new { k.Id, revoked = true };
            }
            case "combos.list":
            {
                var rows = await db.Combos.ToListAsync(ctx.RequestAborted);
                return new
                {
                    combos = rows.Select(c => new
                    { c.Id, c.Name, c.Kind, models = JsonNode.Parse(c.Models) }),
                };
            }
            case "combos.create":
            {
                var comboName = Arg(args, "name");
                var models = args.TryGetProperty("models", out var mEl) ? mEl.GetRawText() : "[]";
                if (comboName is null) return null;
                var now = DateTimeOffset.UtcNow.ToString("o");
                var e = db.Combos.Add(new Combo
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Name = comboName,
                    Kind = Arg(args, "kind") ?? "fallback",
                    Models = models,
                    CreatedAt = now,
                    UpdatedAt = now,
                }).Entity;
                await db.SaveChangesAsync(ctx.RequestAborted);
                return new { e.Id, e.Name };
            }
            case "combos.run":
            case "chat":
            {
                var model = Arg(args, name == "chat" ? "model" : "name") ?? "";
                var prompt = Arg(args, "prompt") ?? "";
                return new { output = await chat(model, prompt) };
            }
            case "usage.stats":
            {
                var q = db.UsageHistory.AsQueryable();
                var total = await q.CountAsync(ctx.RequestAborted);
                var tokens = await q.SumAsync(r => r.PromptTokens + r.CompletionTokens, ctx.RequestAborted);
                var cost = await q.SumAsync(r => r.Cost, ctx.RequestAborted);
                var byProvider = await q.GroupBy(r => r.Provider)
                    .Select(x => new { provider = x.Key, requests = x.Count() }).ToListAsync();
                return new { total, tokens, cost, byProvider };
            }
            case "usage.timeseries":
            {
                var rows = await db.UsageDaily.ToListAsync(ctx.RequestAborted);
                return new { days = rows.Select(r => new { date = r.DateKey, data = JsonNode.Parse(r.Data) }) };
            }
            case "logs.list":
            {
                var limit = int.TryParse(Arg(args, "limit"), out var l) ? Math.Clamp(l, 1, 200) : 50;
                var rows = await db.RequestDetails.OrderByDescending(r => r.Timestamp).Take(limit).ToListAsync(ctx.RequestAborted);
                return new { logs = rows.Select(r => new { r.Id, r.Timestamp, r.Provider, r.Model }) };
            }
            case "settings.get":
            {
                var row = await db.Settings.FirstOrDefaultAsync(ctx.RequestAborted);
                return row is null ? new { } : (object)(JsonNode.Parse(row.Data) ?? new JsonObject());
            }
            case "settings.update":
            {
                var row = await db.Settings.FirstOrDefaultAsync(ctx.RequestAborted)
                    ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
                var cur = JsonNode.Parse(row.Data)!.AsObject();
                if (args.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
                    foreach (var kv2 in JsonNode.Parse(d.GetRawText())!.AsObject())
                        cur[kv2.Key] = kv2.Value?.DeepClone();
                row.Data = cur.ToJsonString();
                await db.SaveChangesAsync(ctx.RequestAborted);
                return new { ok = true };
            }
            case "skills.list":
            {
                var rows = await db.Kv.Where(k => k.Scope == "skills" || k.Scope == "mcpServers").ToListAsync(ctx.RequestAborted);
                return new
                {
                    bundled = new[] { "token-saver", "combo-builder" },
                    registered = rows.Select(r => new { scope = r.Scope, key = r.Key }),
                };
            }
            case "memory.search":
            case "memory.add":
            {
                var row = await db.Kv.FindAsync("memory", "items");
                var items = row is null ? new JsonArray() : JsonNode.Parse(row.Value)!.AsArray();
                if (name == "memory.add")
                {
                    var content = Arg(args, "content");
                    if (content is null) return null;
                    items.Add(new JsonObject
                    {
                        ["id"] = Guid.NewGuid().ToString("N")[..8],
                        ["content"] = content,
                        ["createdAt"] = DateTimeOffset.UtcNow.ToString("o"),
                    });
                    row ??= db.Kv.Add(new KvEntry { Scope = "memory", Key = "items" }).Entity;
                    row.Value = items.ToJsonString();
                    await db.SaveChangesAsync(ctx.RequestAborted);
                    return new { ok = true, count = items.Count };
                }
                var q2 = Arg(args, "q");
                var scored = LLMRouter.Core.Extras.MemorySearch.Search(
                    JsonSerializer.SerializeToElement(items), q2);
                return new { items = scored };
            }
            case "pools.list":
            {
                var rows = await db.ProxyPools.ToListAsync(ctx.RequestAborted);
                return new { pools = rows.Select(r => new { r.Id, r.IsActive, r.TestStatus, data = JsonNode.Parse(r.Data) }) };
            }
            case "token-health.list":
            {
                var rows = await db.ProviderConnections.ToListAsync(ctx.RequestAborted);
                return new
                {
                    connections = rows.Select(c => new
                    { c.Id, c.Provider, c.IsActive, c.UpdatedAt, data = JsonNode.Parse(c.Data)?["testStatus"] }),
                };
            }
            case "docs.search":
            {
                var q3 = Arg(args, "q")?.ToLowerInvariant() ?? "";
                var docs = new[]
                {
                    "endpoint", "providers", "models", "combos", "usage", "settings",
                    "logs", "playground", "oauth", "mcp", "a2a", "conductor",
                    "mitm", "traffic-inspector", "batches", "chaos", "memory",
                    "webhooks", "proxy-pools", "quota", "token-saver", "skills",
                }.Where(d => d.Contains(q3));
                return new { results = docs.Select(d => new { doc = d, url = $"/dashboard/{d}" }) };
            }
            case "system.status":
            {
                return new
                {
                    name = "LLMRouter",
                    version = "0.1",
                    uptimeSec = (long)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
                    counts = new
                    {
                        providers = registry.Providers.Count,
                        connections = await db.ProviderConnections.CountAsync(ctx.RequestAborted),
                        apiKeys = await db.ApiKeys.CountAsync(k => k.IsActive, ctx.RequestAborted),
                        combos = await db.Combos.CountAsync(ctx.RequestAborted),
                        requests = await db.UsageHistory.CountAsync(ctx.RequestAborted),
                    },
                };
            }
            case "gamification.get":
            {
                var requests = await db.UsageHistory.LongCountAsync(ctx.RequestAborted);
                var tokens = await db.UsageHistory.SumAsync(r => r.PromptTokens + r.CompletionTokens, ctx.RequestAborted);
                var provs = await db.ProviderConnections.CountAsync(c => c.IsActive, ctx.RequestAborted);
                var (xp, level, badges) = LLMRouter.Core.Extras.Extras.Gamification(requests, tokens, provs);
                return new { xp, level, badges, requests, tokens, providers = provs };
            }
            case "plugins.list":
            {
                var row = await db.Kv.FindAsync("plugins", "registered");
                var plugins = row is null ? new System.Text.Json.Nodes.JsonArray()
                    : System.Text.Json.Nodes.JsonNode.Parse(row.Value)?.AsArray() ?? new System.Text.Json.Nodes.JsonArray();
                return new { plugins };
            }
            case "plugins.toggle":
            {
                var row = await db.Kv.FindAsync("plugins", "registered");
                var arr = row is null ? new System.Text.Json.Nodes.JsonArray()
                    : System.Text.Json.Nodes.JsonNode.Parse(row.Value)?.AsArray() ?? new System.Text.Json.Nodes.JsonArray();
                var pid = Arg(args, "id") ?? "";
                var en = Arg(args, "enabled") is "true" or "True";
                var hit = false;
                foreach (var p in arr)
                    if (p?["id"]?.GetValue<string>() == pid) { p["enabled"] = en; hit = true; }
                if (!hit) arr.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = pid, ["enabled"] = en });
                if (row is null) db.Kv.Add(new LLMRouter.Core.Data.KvEntry { Scope = "plugins", Key = "registered", Value = arr.ToJsonString() });
                else row.Value = arr.ToJsonString();
                await db.SaveChangesAsync(ctx.RequestAborted);
                return new { ok = true, id = pid, enabled = en };
            }
            case "searchTools.list":
            {
                var conns = await db.ProviderConnections.Where(c => c.IsActive).ToListAsync(ctx.RequestAborted);
                var connTools = conns.Where(c =>
                        (c.Data ?? "").Contains("\"search\"", StringComparison.OrdinalIgnoreCase)
                        || (c.Data ?? "").Contains("\"fetch\"", StringComparison.OrdinalIgnoreCase))
                    .Select(c => new { id = c.Id, c.Provider, c.Name, kind = "connection" });
                var raw = (await db.Kv.FindAsync("searchTools", "list"))?.Value;
                System.Text.Json.Nodes.JsonNode registered;
                try { registered = raw is null ? new System.Text.Json.Nodes.JsonArray() : System.Text.Json.Nodes.JsonNode.Parse(raw)!; }
                catch { registered = new System.Text.Json.Nodes.JsonArray(); }
                return new { tools = connTools, registered };
            }
            case "searchTools.run":
            {
                var id = Arg(args, "id") ?? "";
                var raw = (await db.Kv.FindAsync("searchTools", "list"))?.Value;
                System.Text.Json.Nodes.JsonObject? tool = null;
                if (raw is not null)
                    foreach (var t in System.Text.Json.Nodes.JsonNode.Parse(raw)!.AsArray())
                        if (t?["id"]?.GetValue<string>() == id) tool = t as System.Text.Json.Nodes.JsonObject;
                if (tool is null) return new { error = $"search tool '{id}' not found" };
                if (tool["enabled"]?.GetValue<bool>() == false) return new { error = $"search tool '{id}' is disabled" };
                var kind = tool["kind"]?.GetValue<string>() ?? "webFetch";
                if (kind == "webFetch")
                {
                    var url = Arg(args, "url") ?? tool["url"]?.GetValue<string>();
                    if (url is null) return new { error = "no url provided" };
                    var http = hf.CreateClient("upstream");
                    var resp = await http.GetAsync(url, ctx.RequestAborted);
                    var text = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
                    return new { ok = resp.IsSuccessStatusCode, status = (int)resp.StatusCode, url, content = text[..Math.Min(4000, text.Length)] };
                }
                if (kind == "search")
                {
                    var q = Arg(args, "q") ?? "";
                    var baseUrl = tool["url"]?.GetValue<string>();
                    if (baseUrl is null) return new { error = "search tool has no url" };
                    var http = hf.CreateClient("upstream");
                    var resp = await http.GetAsync($"{baseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(q)}", ctx.RequestAborted);
                    var text = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
                    return new { ok = resp.IsSuccessStatusCode, status = (int)resp.StatusCode, q, content = text[..Math.Min(4000, text.Length)] };
                }
                return new { error = $"unknown kind '{kind}'" };
            }
            case "localCorpus.search":
            {
                // SPEC-032: real corpus search — memory items + docs + skills SKILL.md files,
                // ranked by MemorySearch.Score (term frequency + recency).
                var q4 = Arg(args, "q") ?? "";
                var corpus = new List<JsonElement>();
                var memRaw = (await db.Kv.FindAsync("memory", "items"))?.Value;
                if (memRaw is not null)
                    foreach (var m in JsonDocument.Parse(memRaw).RootElement.EnumerateArray())
                    {
                        var o = m.Clone();
                        corpus.Add(JsonSerializer.SerializeToElement(new
                        { source = "memory", id = o.TryGetProperty("id", out var i) ? i.GetString() : "", content = o.TryGetProperty("content", out var c) ? c.GetString() : "", at = o.TryGetProperty("at", out var a) ? a.GetDateTime() : DateTime.UtcNow }));
                    }
                foreach (var doc in new[] { "endpoint", "providers", "models", "combos", "usage", "settings", "logs", "playground", "oauth", "mcp", "a2a", "conductor", "mitm", "traffic-inspector", "batches", "chaos", "memory", "webhooks", "proxy-pools", "quota", "token-saver", "skills" })
                    corpus.Add(JsonSerializer.SerializeToElement(new { source = "doc", id = doc, content = doc.Replace('-', ' '), at = DateTime.UtcNow.AddDays(-1) }));
                var skillsDir = Path.Combine(ctx.RequestServices
                    .GetRequiredService<IWebHostEnvironment>().ContentRootPath, "skills");
                if (Directory.Exists(skillsDir))
                    foreach (var f in Directory.EnumerateFiles(skillsDir, "SKILL.md", SearchOption.AllDirectories).Take(50))
                    {
                        var txt = await File.ReadAllTextAsync(f, ctx.RequestAborted);
                        corpus.Add(JsonSerializer.SerializeToElement(new { source = "skill", id = Path.GetFileName(Path.GetDirectoryName(f)) ?? f, content = txt[..Math.Min(2000, txt.Length)], at = File.GetLastWriteTimeUtc(f) }));
                    }
                var hits = LLMRouter.Core.Extras.MemorySearch.Search(
                    JsonSerializer.SerializeToElement(corpus), q4, 20);
                return new { results = hits.Select(h => JsonNode.Parse(h.GetRawText())), count = hits.Count };
            }
            default:
                return null;
        }
    }
}
