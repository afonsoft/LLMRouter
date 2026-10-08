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
                    using var resp = await c.SendAsync(new HttpRequestMessage(HttpMethod.Get, url));
                    return new { id = p.Id, reachable = true, status = (int)resp.StatusCode };
                }
                catch (Exception ex) { return new { id = p.Id, reachable = false, error = ex.Message }; }
            }
            case "models.list":
            {
                var combos = await db.Combos.Select(c => c.Name).ToListAsync();
                var aliases = await db.Kv.Where(k => k.Scope == "modelAliases").ToListAsync();
                return new { models = combos, aliases = aliases.Select(a => a.Key) };
            }
            case "connections.list":
            {
                var q = db.ProviderConnections.AsQueryable();
                if (Arg(args, "provider") is { } prov) q = q.Where(c => c.Provider == prov);
                var rows = await q.ToListAsync();
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
                await db.SaveChangesAsync();
                return new { e.Id, e.Provider };
            }
            case "apiKeys.list":
            {
                var keys = await db.ApiKeys.ToListAsync();
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
                await db.SaveChangesAsync();
                return new { e.Id, key };
            }
            case "apiKeys.revoke":
            {
                var k = await db.ApiKeys.FindAsync(Arg(args, "id"));
                if (k is null) return null;
                k.IsActive = false;
                await db.SaveChangesAsync();
                return new { k.Id, revoked = true };
            }
            case "combos.list":
            {
                var rows = await db.Combos.ToListAsync();
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
                await db.SaveChangesAsync();
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
                var total = await q.CountAsync();
                var tokens = await q.SumAsync(r => r.PromptTokens + r.CompletionTokens);
                var cost = await q.SumAsync(r => r.Cost);
                var byProvider = await q.GroupBy(r => r.Provider)
                    .Select(x => new { provider = x.Key, requests = x.Count() }).ToListAsync();
                return new { total, tokens, cost, byProvider };
            }
            case "usage.timeseries":
            {
                var rows = await db.UsageDaily.ToListAsync();
                return new { days = rows.Select(r => new { date = r.DateKey, data = JsonNode.Parse(r.Data) }) };
            }
            case "logs.list":
            {
                var limit = int.TryParse(Arg(args, "limit"), out var l) ? Math.Clamp(l, 1, 200) : 50;
                var rows = await db.RequestDetails.OrderByDescending(r => r.Timestamp).Take(limit).ToListAsync();
                return new { logs = rows.Select(r => new { r.Id, r.Timestamp, r.Provider, r.Model }) };
            }
            case "settings.get":
            {
                var row = await db.Settings.FirstOrDefaultAsync();
                return row is null ? new { } : (object)(JsonNode.Parse(row.Data) ?? new JsonObject());
            }
            case "settings.update":
            {
                var row = await db.Settings.FirstOrDefaultAsync()
                    ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
                var cur = JsonNode.Parse(row.Data)!.AsObject();
                if (args.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
                    foreach (var kv2 in JsonNode.Parse(d.GetRawText())!.AsObject())
                        cur[kv2.Key] = kv2.Value?.DeepClone();
                row.Data = cur.ToJsonString();
                await db.SaveChangesAsync();
                return new { ok = true };
            }
            case "skills.list":
            {
                var rows = await db.Kv.Where(k => k.Scope == "skills" || k.Scope == "mcpServers").ToListAsync();
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
                    await db.SaveChangesAsync();
                    return new { ok = true, count = items.Count };
                }
                var q2 = Arg(args, "q")?.ToLowerInvariant();
                var hits = items.Where(i => q2 is null ||
                    (i?["content"]?.GetValue<string>().Contains(q2, StringComparison.OrdinalIgnoreCase) ?? false));
                return new { items = hits };
            }
            case "pools.list":
            {
                var rows = await db.ProxyPools.ToListAsync();
                return new { pools = rows.Select(r => new { r.Id, r.IsActive, r.TestStatus, data = JsonNode.Parse(r.Data) }) };
            }
            case "token-health.list":
            {
                var rows = await db.ProviderConnections.ToListAsync();
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
                        connections = await db.ProviderConnections.CountAsync(),
                        apiKeys = await db.ApiKeys.CountAsync(k => k.IsActive),
                        combos = await db.Combos.CountAsync(),
                        requests = await db.UsageHistory.CountAsync(),
                    },
                };
            }
            default:
                return null;
        }
    }
}
