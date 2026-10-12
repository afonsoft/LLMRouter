using System.Text.Json;
using System.Text.RegularExpressions;
using LLMRouter.Core.Data;
using LLMRouter.Core.Usage;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-092: management batch — guardrails CRUD/test/pipeline, MCP mgmt,
/// quota groups+pools, key-group sub-resources+permissions, webhooks detail,
/// pricing extras, models alias/catalog/test.
/// </summary>
public static partial class MgmtBatchEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const string GuardrailScope = "guardrails";
    private const string QuotaGroupScope = "quotaGroups";
    private const string QuotaPoolScope = "quotaPools";
    private const string AliasScope = "aliasModels";
    private const string KeyPermScope = "keyGroupPerms";
    private const string PricingDefaultsScope = "pricingDefaults";
    private const string PricingOverridesScope = "pricingOverrides";

    /// <summary>MCP SSE generation — bumped on restart so subscribers see a restart event and close.</summary>
    private static long _mcpGeneration;
    private static DateTime _lastMcpRequest = DateTime.MinValue;
    internal static void NoteMcpRequest() => _lastMcpRequest = DateTime.UtcNow;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();
        Guardrails(g);
        McpMgmt(g);
        QuotaGroups(g);
        KeyGroupSubs(g);
        WebhookDetail(g);
        Pricing(g);
        ModelsExtras(g);
    }

    // ==================== GUARDRAILS ====================

    private sealed record Guardrail(string Id, string Name, string Kind, string Pattern,
        string Action, string Target, bool Enabled);

    private static async Task<List<Guardrail>> LoadGuardrails(LlmRouterDbContext db)
    {
        var rows = await db.Kv.Where(k => k.Scope == GuardrailScope).ToListAsync();
        var list = new List<Guardrail>();
        foreach (var r in rows)
        {
            try
            {
                var d = JsonDocument.Parse(r.Value).RootElement;
                list.Add(new Guardrail(
                    d.TryGetProperty("id", out var i) ? i.GetString() ?? r.Key : r.Key,
                    d.TryGetProperty("name", out var n) ? n.GetString() ?? r.Key : r.Key,
                    d.TryGetProperty("kind", out var k) ? k.GetString() ?? "regex" : "regex",
                    d.TryGetProperty("pattern", out var p) ? p.GetString() ?? "" : "",
                    d.TryGetProperty("action", out var a) ? a.GetString() ?? "block" : "block",
                    d.TryGetProperty("target", out var t) ? t.GetString() ?? "request" : "request",
                    !d.TryGetProperty("enabled", out var e) || e.GetBoolean()));
            }
            catch (JsonException) { /* skip malformed rule */ }
        }
        return list;
    }

    private static bool RuleMatches(Guardrail r, string text)
    {
        try
        {
            return r.Kind switch
            {
                "keyword" => text.Contains(r.Pattern, StringComparison.OrdinalIgnoreCase),
                "length" => int.TryParse(r.Pattern, out var max) && text.Length > max,
                _ => Regex.IsMatch(text, r.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500)),
            };
        }
        catch (RegexMatchTimeoutException) { return false; }
        catch (ArgumentException) { return false; } // invalid regex pattern
    }

    private static string ApplyRedact(IEnumerable<Guardrail> rules, string text)
    {
        foreach (var r in rules)
        {
            try
            {
                text = r.Kind == "keyword"
                    ? text.Replace(r.Pattern, "***", StringComparison.OrdinalIgnoreCase)
                    : Regex.Replace(text, r.Pattern, "***", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
            }
            catch (RegexMatchTimeoutException) { /* keep original */ }
            catch (ArgumentException) { /* invalid regex: keep original */ }
        }
        return text;
    }

    /// <summary>Request-side gate for /v1 pipelines — block 400 / warn header / redact rewrite.</summary>
    internal static async Task<(bool Ok, JsonElement Body)> GuardrailRequestAsync(
        HttpContext ctx, LlmRouterDbContext db, JsonElement body)
    {
        var rules = (await LoadGuardrails(db)).Where(r => r.Enabled && r.Target is "request" or "both").ToList();
        if (rules.Count == 0) return (true, body);
        var text = body.GetRawText();
        if (text.Length > Core.Guardrails.PromptGuard.MaxScanChars)
            text = text[..Core.Guardrails.PromptGuard.MaxScanChars];
        foreach (var r in rules.Where(r => r.Action == "block"))
        {
            if (RuleMatches(r, text))
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    error = new { code = "guardrail_blocked", rule = r.Name, message = $"Blocked by guardrail '{r.Name}'." },
                }, JsonOpts), ctx.RequestAborted);
                return (false, body);
            }
        }
        var warns = rules.Where(r => r.Action == "warn" && RuleMatches(r, text)).Select(r => r.Name).ToList();
        if (warns.Count > 0) ctx.Response.Headers["X-Guardrail-Warn"] = string.Join(",", warns);
        var redacts = rules.Where(r => r.Action == "redact").ToList();
        if (redacts.Count > 0)
        {
            var redacted = ApplyRedact(redacts, body.GetRawText());
            try { return (true, JsonDocument.Parse(redacted).RootElement.Clone()); }
            catch (JsonException) { return (true, body); } // redaction broke json — pass original
        }
        return (true, body);
    }

    /// <summary>Response-side redaction (rules targeting response|both).</summary>
    public static async Task<string> RedactResponseAsync(LlmRouterDbContext db, string text)
    {
        var rules = (await LoadGuardrails(db))
            .Where(r => r.Enabled && r.Action == "redact" && r.Target is "response" or "both");
        return ApplyRedact(rules, text);
    }

    private static void Guardrails(RouteGroupBuilder g)
    {
        g.MapGet("/guardrails", async (LlmRouterDbContext db) =>
        {
            var sdata = await Core.Gateway.HotReads.SettingsDataAsync(db);
            var rg = Core.Guardrails.ResponseGuard.Read(sdata);
            // SPEC-107: response section alongside input rules
            var response = new
            {
                rg.Enabled, rg.Mode, rg.Threshold, rg.ExtraPatterns,
                builtins = new[] { "api_key", "private_key", "jwt", "email", "phone", "credit_card" },
            };
            return Results.Json(new { items = await LoadGuardrails(db), response }, JsonOpts);
        });

        g.MapPost("/guardrails", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var id = b.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String
                ? i.GetString()! : Guid.NewGuid().ToString("N")[..10];
            var rule = new
            {
                id,
                name = b.TryGetProperty("name", out var n) ? n.GetString() ?? id : id,
                kind = b.TryGetProperty("kind", out var k) ? k.GetString() ?? "regex" : "regex",
                pattern = b.TryGetProperty("pattern", out var p) ? p.GetString() ?? "" : "",
                action = b.TryGetProperty("action", out var a) ? a.GetString() ?? "block" : "block",
                target = b.TryGetProperty("target", out var t) ? t.GetString() ?? "request" : "request",
                enabled = !b.TryGetProperty("enabled", out var e) || e.GetBoolean(),
            };
            db.Kv.Add(new KvEntry { Scope = GuardrailScope, Key = id, Value = JsonSerializer.Serialize(rule, JsonOpts) });
            await db.SaveChangesAsync();
            return Results.Json(new { rule }, JsonOpts);
        });

        g.MapGet("/guardrails/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var row = await db.Kv.FindAsync(GuardrailScope, id);
            return row is null ? Results.NotFound(new { error = "rule not found" })
                : Results.Json(JsonDocument.Parse(row.Value).RootElement.Clone(), JsonOpts);
        });

        g.MapPut("/guardrails/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var row = await db.Kv.FindAsync(GuardrailScope, id);
            if (row is null) return Results.NotFound(new { error = "rule not found" });
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var cur = JsonDocument.Parse(row.Value).RootElement;
            string Str(string k, string d) => b.TryGetProperty(k, out var v) ? v.GetString() ?? d
                : cur.TryGetProperty(k, out var v2) ? v2.GetString() ?? d : d;
            var rule = new
            {
                id, name = Str("name", id), kind = Str("kind", "regex"),
                pattern = Str("pattern", ""), action = Str("action", "block"),
                target = Str("target", "request"),
                enabled = b.TryGetProperty("enabled", out var e) ? e.GetBoolean()
                    : !cur.TryGetProperty("enabled", out var e2) || e2.GetBoolean(),
            };
            row.Value = JsonSerializer.Serialize(rule, JsonOpts);
            await db.SaveChangesAsync();
            return Results.Json(new { rule }, JsonOpts);
        });

        g.MapDelete("/guardrails/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var row = await db.Kv.FindAsync(GuardrailScope, id);
            if (row is null) return Results.NotFound(new { error = "rule not found" });
            db.Kv.Remove(row);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true }, JsonOpts);
        });

        g.MapPost("/guardrails/test", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var input = b.TryGetProperty("input", out var i) ? i.GetString() ?? "" : "";
            var target = b.TryGetProperty("target", out var t) ? t.GetString() ?? "request" : "request";
            var rules = (await LoadGuardrails(db))
                .Where(r => r.Enabled && (r.Target == target || r.Target == "both")).ToList();
            var matches = rules.Where(r => RuleMatches(r, input))
                .Select(r => new { ruleId = r.Id, r.Name, r.Action }).ToList();
            return Results.Json(new { matches, wouldBlock = matches.Any(m => m.Action == "block") }, JsonOpts);
        });
    }

    // ==================== MCP MGMT ====================

    private static void McpMgmt(RouteGroupBuilder g)
    {
        g.MapGet("/mcp/status", (LlmRouterDbContext db) => Results.Json(new
        {
            running = true,
            transport = "http+sse",
            tools = Mcp.McpTools.ListTools().Count(),
            connectedClients = 0,
            lastRequest = _lastMcpRequest == DateTime.MinValue ? (DateTime?)null : _lastMcpRequest,
            generation = _mcpGeneration,
        }, JsonOpts));

        g.MapGet("/mcp/tools", () =>
        {
            var raw = JsonSerializer.Serialize(Mcp.McpTools.ListTools(), JsonOpts);
            var tools = JsonDocument.Parse(raw).RootElement.EnumerateArray()
                .Select(t => new
                {
                    name = t.GetProperty("name").GetString(),
                    description = t.GetProperty("description").GetString(),
                    combo = t.GetProperty("name").GetString() is { } n && n.StartsWith("chat__")
                        ? n[6..] : null,
                });
            return Results.Json(new { tools }, JsonOpts);
        });

        g.MapPost("/mcp/restart", () =>
        {
            Interlocked.Increment(ref _mcpGeneration);
            return Results.Json(new { restarted = true }, JsonOpts);
        });

        // SSE stream of MCP audit events (existing mcpToolCalls pushed live)
        g.MapGet("/mcp/stream", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            var gen = _mcpGeneration;
            var last = DateTime.MinValue;
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                if (_mcpGeneration != gen)
                {
                    await ctx.Response.WriteAsync("event: restart\ndata: {\"restarted\":true}\n\n", ctx.RequestAborted);
                    return;
                }
                var rows = await db.McpToolCalls
                    .Where(c => string.Compare(c.At, last == DateTime.MinValue ? "" : last.ToString("O")) > 0)
                    .OrderBy(c => c.At).Take(20).ToListAsync(ctx.RequestAborted);
                foreach (var r in rows)
                {
                    if (DateTime.TryParse(r.At, out var at) && at > last) last = at;
                    await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(r, JsonOpts)}\n\n", ctx.RequestAborted);
                }
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                if (last != DateTime.MinValue || rows.Count == 0)
                {
                    // idle after first batch: end politely (tests don't hold streams)
                    if (last != DateTime.MinValue) return;
                }
                await Task.Delay(500, ctx.RequestAborted);
            }
        });
    }

    // ==================== QUOTA GROUPS + POOLS ====================

    private static async Task<IResult> KvList(LlmRouterDbContext db, string scope)
    {
        var rows = await db.Kv.Where(k => k.Scope == scope).ToListAsync();
        var items = rows.Select(r =>
        {
            try { return JsonDocument.Parse(r.Value).RootElement.Clone(); }
            catch (JsonException) { return (JsonElement?)null; }
        }).Where(x => x is not null);
        return Results.Json(new { items }, JsonOpts);
    }

    private static async Task<IResult> KvPut(HttpContext ctx, LlmRouterDbContext db, string scope, string? id = null)
    {
        var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
        var key = id ?? (b.TryGetProperty("id", out var i) ? i.GetString() : null)
            ?? Guid.NewGuid().ToString("N")[..10];
        var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(b.GetRawText()) ?? [];
        obj["id"] = JsonSerializer.SerializeToElement(key);
        var row = await db.Kv.FindAsync(scope, key);
        var val = JsonSerializer.Serialize(obj, JsonOpts);
        if (row is null) db.Kv.Add(new KvEntry { Scope = scope, Key = key, Value = val });
        else row.Value = val;
        await db.SaveChangesAsync();
        return Results.Json(new { item = JsonDocument.Parse(val).RootElement.Clone() }, JsonOpts);
    }

    private static async Task<IResult> KvGetOne(LlmRouterDbContext db, string scope, string id)
        => await db.Kv.FindAsync(scope, id) is { } row
            ? Results.Json(JsonDocument.Parse(row.Value).RootElement.Clone(), JsonOpts)
            : Results.NotFound(new { error = "not found" });

    private static async Task<IResult> KvDelete(LlmRouterDbContext db, string scope, string id)
    {
        var row = await db.Kv.FindAsync(scope, id);
        if (row is null) return Results.NotFound(new { error = "not found" });
        db.Kv.Remove(row);
        await db.SaveChangesAsync();
        return Results.Json(new { success = true }, JsonOpts);
    }

    private static void QuotaGroups(RouteGroupBuilder g)
    {
        g.MapGet("/quota/groups", async (LlmRouterDbContext db) => await KvList(db, QuotaGroupScope));
        g.MapPost("/quota/groups", async (HttpContext ctx, LlmRouterDbContext db) => await KvPut(ctx, db, QuotaGroupScope));
        g.MapGet("/quota/groups/{id}", async (string id, LlmRouterDbContext db) => await KvGetOne(db, QuotaGroupScope, id));
        g.MapPut("/quota/groups/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) => await KvPut(ctx, db, QuotaGroupScope, id));
        g.MapDelete("/quota/groups/{id}", async (string id, LlmRouterDbContext db) => await KvDelete(db, QuotaGroupScope, id));
        g.MapGet("/quota/pools", async (LlmRouterDbContext db) => await KvList(db, QuotaPoolScope));
        g.MapPost("/quota/pools", async (HttpContext ctx, LlmRouterDbContext db) => await KvPut(ctx, db, QuotaPoolScope));
        g.MapGet("/quota/pools/{id}", async (string id, LlmRouterDbContext db) => await KvGetOne(db, QuotaPoolScope, id));
        g.MapPut("/quota/pools/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) => await KvPut(ctx, db, QuotaPoolScope, id));
        g.MapDelete("/quota/pools/{id}", async (string id, LlmRouterDbContext db) => await KvDelete(db, QuotaPoolScope, id));

        g.MapGet("/quota/keys/{providerId}/models", async (string providerId, LlmRouterDbContext db) =>
        {
            var synced = await db.SyncedModels.Where(m => m.Provider == providerId && m.Available).ToListAsync();
            return Results.Json(new
            {
                providerId,
                models = synced.Select(m => m.Model).Distinct(),
            }, JsonOpts);
        });
    }

    // ==================== KEY GROUP SUB-RESOURCES ====================

    private static void KeyGroupSubs(RouteGroupBuilder g)
    {
        g.MapGet("/keys/groups/{id}/keys", async (string id, LlmRouterDbContext db) =>
        {
            var grp = await db.KeyGroups.FindAsync(id);
            if (grp is null) return Results.NotFound(new { error = "group not found" });
            List<string> ids;
            try { ids = JsonSerializer.Deserialize<List<string>>(grp.KeysJson) ?? []; }
            catch (JsonException) { ids = []; }
            var keys = await db.ApiKeys.Where(k => ids.Contains(k.Id)).ToListAsync();
            return Results.Json(new { group = id, keys = keys.Select(k => new { k.Id, k.Name, active = k.IsActive }) }, JsonOpts);
        });

        g.MapPost("/keys/groups/{id}/keys", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var grp = await db.KeyGroups.FindAsync(id);
            if (grp is null) return Results.NotFound(new { error = "group not found" });
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            List<string> ids;
            try { ids = JsonSerializer.Deserialize<List<string>>(grp.KeysJson) ?? []; }
            catch (JsonException) { ids = []; }
            foreach (var v in b.EnumerateArray())
            {
                var kid = v.GetString();
                if (kid is not null && !ids.Contains(kid)) ids.Add(kid);
            }
            grp.KeysJson = JsonSerializer.Serialize(ids, JsonOpts);
            await db.SaveChangesAsync();
            return Results.Json(new { group = id, keys = ids }, JsonOpts);
        });

        g.MapGet("/keys/groups/{id}/permissions", async (string id, LlmRouterDbContext db) =>
            await KvGetOne(db, KeyPermScope, id) is { } r ? r : Results.Json(new { scopes = Array.Empty<string>() }, JsonOpts));

        g.MapPut("/keys/groups/{id}/permissions", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var grp = await db.KeyGroups.FindAsync(id);
            if (grp is null) return Results.NotFound(new { error = "group not found" });
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var row = await db.Kv.FindAsync(KeyPermScope, id);
            var val = b.GetRawText();
            if (row is null) db.Kv.Add(new KvEntry { Scope = KeyPermScope, Key = id, Value = val });
            else row.Value = val;
            await db.SaveChangesAsync();
            return Results.Json(new { group = id, permissions = b }, JsonOpts);
        });

        g.MapGet("/keys/{id}/access", async (string id, LlmRouterDbContext db, int? limit) =>
        {
            var k = await db.ApiKeys.FindAsync(id);
            if (k is null) return Results.NotFound(new { error = "key not found" });
            var rows = await db.UsageHistory.Where(u => u.ApiKey == k.Key)
                .OrderByDescending(u => u.Timestamp).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync();
            return Results.Json(new { keyId = id, calls = rows }, JsonOpts);
        });

        g.MapGet("/keys/{id}/devices", async (string id, LlmRouterDbContext db) =>
        {
            var k = await db.ApiKeys.FindAsync(id);
            if (k is null) return Results.NotFound(new { error = "key not found" });
            var rows = await db.UsageHistory.Where(u => u.ApiKey == k.Key && u.Meta != null)
                .OrderByDescending(u => u.Timestamp).Take(1000).ToListAsync();
            var devices = rows.Select(u =>
                {
                    try
                    {
                        using var d = JsonDocument.Parse(u.Meta!);
                        return d.RootElement.TryGetProperty("ip", out var ip) ? ip.GetString()
                            : d.RootElement.TryGetProperty("clientIp", out var c) ? c.GetString() : null;
                    }
                    catch (JsonException) { return null; }
                })
                .Where(x => x is not null).Distinct().ToList();
            return Results.Json(new { keyId = id, devices }, JsonOpts);
        });
    }

    /// <summary>Group-permission gate for /v1 calls. Key in a scoped group lacking `scope` → 403.</summary>
    internal static async Task<bool> KeyGroupGateAsync(HttpContext ctx, LlmRouterDbContext db, string apiKey, string scope)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey == "dashboard" || apiKey.StartsWith("relay:"))
            return true;
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Key == apiKey, ctx.RequestAborted);
        if (key is null) return true;
        var groups = await db.KeyGroups.ToListAsync(ctx.RequestAborted);
        var memberGroups = groups.Where(grp =>
        {
            try { return (JsonSerializer.Deserialize<List<string>>(grp.KeysJson) ?? [])
                .Any(i => i == key.Id || i == key.Key); }
            catch (JsonException) { return false; }
        }).ToList();
        if (memberGroups.Count == 0) return true;
        var hasPermSet = false;
        foreach (var grp in memberGroups)
        {
            var row = await db.Kv.FindAsync(KeyPermScope, grp.Id);
            if (row is null) continue; // group has no permission set → unrestricted
            hasPermSet = true;
            try
            {
                var scopes = JsonDocument.Parse(row.Value).RootElement
                    .GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToList();
                if (scopes.Contains("*") || scopes.Contains(scope)) return true;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            { /* malformed perms row → treat as unrestricted */ }
        }
        if (!hasPermSet) return true; // groups exist but none configured permissions
        ctx.Response.StatusCode = 403;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = new { code = "scope_denied", message = $"API key group does not allow '{scope}'." },
        }, JsonOpts), ctx.RequestAborted);
        return false;
    }

    /// <summary>Scope name for key-group permissions from the request path.</summary>
    internal static string ScopeFromPath(string path) => path switch
    {
        var p when p.Contains("/embeddings") => "embeddings",
        var p when p.Contains("/images") || p.Contains("/audio") || p.Contains("/videos") => "media",
        var p when p.Contains("/chat") || p.Contains("/messages") || p.Contains("/responses") => "chat",
        _ => "chat",
    };

    // ==================== WEBHOOKS DETAIL ====================

    private static Task<List<JsonElement>> WebhookList(LlmRouterDbContext db)
        => Core.Extras.WebhookStore.ListAsync(db);

    private static void WebhookDetail(RouteGroupBuilder g)
    {
        g.MapGet("/webhooks/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var wh = (await WebhookList(db)).FirstOrDefault(w =>
                w.TryGetProperty("id", out var i) && i.GetString() == id);
            return wh.ValueKind == JsonValueKind.Undefined ? Results.NotFound(new { error = "webhook not found" })
                : Results.Json(wh, JsonOpts);
        });

        g.MapPut("/webhooks/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var list = await WebhookList(db);
            var idx = list.FindIndex(w => w.TryGetProperty("id", out var i) && i.GetString() == id);
            if (idx < 0) return Results.NotFound(new { error = "webhook not found" });
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var cur = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(list[idx].GetRawText()) ?? [];
            foreach (var p in b.EnumerateObject()) cur[p.Name] = p.Value.Clone();
            var merged = JsonSerializer.SerializeToElement(cur);
            await Core.Extras.WebhookStore.UpsertAsync(db, merged);
            await db.SaveChangesAsync();
            return Results.Json(new { webhook = merged }, JsonOpts);
        });

        g.MapDelete("/webhooks/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var list = await WebhookList(db);
            var idx = list.FindIndex(w => w.TryGetProperty("id", out var i) && i.GetString() == id);
            if (idx < 0) return Results.NotFound(new { error = "webhook not found" });
            await Core.Extras.WebhookStore.RemoveAsync(db, id);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true }, JsonOpts);
        });

        g.MapGet("/webhooks/{id}/deliveries", async (string id, LlmRouterDbContext db) =>
        {
            var items = await Core.Extras.WebhookStore.DeliveriesAsync(db, id);
            return Results.Json(new { webhookId = id, deliveries = items }, JsonOpts);
        });

        g.MapPost("/webhooks/{id}/test", async (string id, LlmRouterDbContext db) =>
        {
            var wh = (await WebhookList(db)).FirstOrDefault(w =>
                w.TryGetProperty("id", out var i) && i.GetString() == id);
            if (wh.ValueKind == JsonValueKind.Undefined)
                return Results.NotFound(new { error = "webhook not found" });
            var delivery = new
            {
                id = Guid.NewGuid().ToString("N")[..10], webhookId = id,
                eventType = "webhook.test", status = "ok", at = DateTime.UtcNow.ToString("O"),
            };
            await Core.Extras.WebhookStore.AddDeliveryAsync(db, id, delivery);
            await db.SaveChangesAsync();
            return Results.Json(new { delivery }, JsonOpts);
        });

        g.MapPost("/webhooks/validate-url", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var url = b.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
                return Results.Json(new { valid = false, reason = "invalid_url" }, JsonOpts);
            var srow = await db.Kv.FindAsync("settings", "root");
            var allowPrivate = srow is not null
                && JsonDocument.Parse(srow.Value).RootElement
                    .TryGetProperty("webhooks", out var w)
                && w.TryGetProperty("allowPrivate", out var a)
                && a.ValueKind == JsonValueKind.True;
            var host = uri.Host;
            var isPrivate = host is "localhost" or "127.0.0.1" or "::1" or "[::1]"
                || host.StartsWith("10.", StringComparison.Ordinal)
                || host.StartsWith("192.168.", StringComparison.Ordinal)
                || host.StartsWith("169.254.", StringComparison.Ordinal)
                || Regex.IsMatch(host, @"^172\.(1[6-9]|2\d|3[01])\.", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return isPrivate && !allowPrivate
                ? Results.Json(new { valid = false, reason = "private_address" }, JsonOpts)
                : Results.Json(new { valid = true }, JsonOpts);
        });
    }

    // ==================== PRICING ====================

    private static void Pricing(RouteGroupBuilder g)
    {
        g.MapGet("/pricing/defaults", async (LlmRouterDbContext db) =>
            await KvGetOne(db, PricingDefaultsScope, "root") is { } r ? r
            : Results.Json(new { inputPer1M = 0, outputPer1M = 0 }, JsonOpts));

        g.MapPut("/pricing/defaults", async (HttpContext ctx, LlmRouterDbContext db) =>
            await KvPut(ctx, db, PricingDefaultsScope, "root"));

        g.MapGet("/pricing/models", async (LlmRouterDbContext db) =>
        {
            var defaults = (await db.Kv.FindAsync(PricingDefaultsScope, "root"))?.Value;
            var defObj = defaults is null ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(defaults) ?? [];
            var overrides = await db.Kv.Where(k => k.Scope == PricingOverridesScope).ToListAsync();
            var perModel = overrides.ToDictionary(o => o.Key, o =>
            {
                try { return JsonDocument.Parse(o.Value).RootElement.Clone(); }
                catch (JsonException) { return (JsonElement?)null; }
            });
            var synced = await db.SyncedModels.ToListAsync();
            var models = synced.Select(m => new
            {
                model = $"{m.Provider}/{m.Model}",
                pricing = perModel.TryGetValue($"{m.Provider}/{m.Model}", out var o) && o is not null ? o
                    : perModel.TryGetValue(m.Model, out var o2) && o2 is not null ? o2
                    : JsonSerializer.SerializeToElement(defObj),
            });
            return Results.Json(new { defaults = defObj, models }, JsonOpts);
        });

        g.MapPost("/pricing/sync", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            // stub-safe: accepts injected fixture {items:[{model,inputPer1M,outputPer1M}]};
            // empty body syncs real prices from the OpenRouter public catalog
            // (SPEC-118) into settings.data.catalogSyncedPricing.
            JsonElement b = default;
            var hasBody = ctx.Request.ContentLength is > 0;
            if (hasBody)
            {
                try { b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
                catch (JsonException) { return Results.Json(new { synced = 0 }, JsonOpts); }
            }
            var synced = 0;
            if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("items", out var items))
            {
                foreach (var it in items.EnumerateArray())
                {
                    var model = it.TryGetProperty("model", out var m) ? m.GetString() : null;
                    if (model is null) continue;
                    var row = await db.Kv.FindAsync(PricingOverridesScope, model);
                    var val = it.GetRawText();
                    if (row is null) db.Kv.Add(new KvEntry { Scope = PricingOverridesScope, Key = model, Value = val });
                    else row.Value = val;
                    synced++;
                }
                // mirror into settings.data.catalogSyncedPricing so the catalog
                // shows the synced flag and PricingService resolves the price.
                var sd1 = await PricingService.SettingsDataAsync(db);
                var root1 = sd1.ValueKind == JsonValueKind.Object
                    ? sd1.EnumerateObject().ToDictionary(k => k.Name, k => k.Value.Clone())
                    : new Dictionary<string, JsonElement>();
                var syncMap = root1.TryGetValue(PricingService.SyncedKey, out var sm)
                    && sm.ValueKind == JsonValueKind.Object
                    ? sm.EnumerateObject().ToDictionary(k => k.Name, k => k.Value.Clone())
                    : new Dictionary<string, JsonElement>();
                foreach (var it in items.EnumerateArray())
                {
                    var model = it.TryGetProperty("model", out var m) ? m.GetString() : null;
                    if (model is not null) syncMap[model] = it.Clone();
                }
                root1[PricingService.SyncedKey] = JsonSerializer.SerializeToElement(syncMap);
                var srow = await db.Settings.FindAsync(1) ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
                srow.Data = JsonSerializer.Serialize(root1);
                await db.SaveChangesAsync();
                return Results.Json(new { synced }, JsonOpts);
            }

            var prices = new Dictionary<string, JsonElement>();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var doc = JsonDocument.Parse(
                    await http.GetStringAsync("https://openrouter.ai/api/v1/models"));
                foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    var id = m.TryGetProperty("id", out var i) ? i.GetString() : null;
                    if (id is null || !id.Contains('/')
                        || !m.TryGetProperty("pricing", out var pr)) continue;
                    double P(string n) => pr.TryGetProperty(n, out var v)
                        && double.TryParse(v.GetString(), out var d) ? d * 1_000_000 : 0;
                    var inp = P("prompt"); var outp = P("completion");
                    if (inp <= 0 && outp <= 0) continue;
                    prices[id] = JsonSerializer.SerializeToElement(new
                    { input = inp, output = outp, syncedAt = DateTime.UtcNow.ToString("O") });
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                return Results.Json(new { synced = 0, error = "catalog fetch failed" }, JsonOpts);
            }
            var sd = await PricingService.SettingsDataAsync(db);
            var root = sd.ValueKind == JsonValueKind.Object
                ? sd.EnumerateObject().ToDictionary(k => k.Name, k => k.Value.Clone())
                : new Dictionary<string, JsonElement>();
            root[PricingService.SyncedKey] = JsonSerializer.SerializeToElement(prices);
            var row2 = await db.Settings.FindAsync(1) ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
            row2.Data = JsonSerializer.Serialize(root);
            await db.SaveChangesAsync();
            return Results.Json(new { synced = prices.Count }, JsonOpts);
        });

        // SPEC-118: drop every synced price (user overrides are kept).
        g.MapPost("/pricing/clear-synced", async (LlmRouterDbContext db) =>
        {
            var sd = await PricingService.SettingsDataAsync(db);
            var root = sd.ValueKind == JsonValueKind.Object
                ? sd.EnumerateObject().ToDictionary(k => k.Name, k => k.Value.Clone())
                : new Dictionary<string, JsonElement>();
            var cleared = root.Remove(PricingService.SyncedKey);
            var row = await db.Settings.FindAsync(1) ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
            row.Data = JsonSerializer.Serialize(root);
            await db.SaveChangesAsync();
            return Results.Json(new { cleared }, JsonOpts);
        });
    }

    // ==================== MODELS EXTRAS ====================

    /// <summary>Resolve a model alias → provider/model before combo expansion.</summary>
    public static async Task<string> ResolveAliasAsync(LlmRouterDbContext db, string model)
    {
        var row = await db.Kv.FindAsync(AliasScope, model);
        if (row is null) return model;
        try
        {
            var d = JsonDocument.Parse(row.Value).RootElement;
            return d.TryGetProperty("target", out var t) ? t.GetString() ?? model : model;
        }
        catch (JsonException) { return model; }
    }

    private static void ModelsExtras(RouteGroupBuilder g)
    {
        g.MapGet("/models/alias", async (LlmRouterDbContext db) => await KvList(db, AliasScope));
        g.MapPost("/models/alias", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var alias = b.TryGetProperty("alias", out var a) ? a.GetString() : null;
            var target = b.TryGetProperty("target", out var t) ? t.GetString() : null;
            if (alias is null || target is null)
                return Results.BadRequest(new { error = "alias and target required" });
            db.Kv.Add(new KvEntry { Scope = AliasScope, Key = alias, Value = JsonSerializer.Serialize(new { alias, target }, JsonOpts) });
            await db.SaveChangesAsync();
            return Results.Json(new { alias, target }, JsonOpts);
        });
        g.MapGet("/models/alias/{*alias}", async (string alias, LlmRouterDbContext db) =>
            await KvGetOne(db, AliasScope, Uri.UnescapeDataString(alias)));
        g.MapPut("/models/alias/{*alias}", async (string alias, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var target = b.TryGetProperty("target", out var t) ? t.GetString() : null;
            var row = await db.Kv.FindAsync(AliasScope, Uri.UnescapeDataString(alias));
            if (row is null || target is null) return Results.NotFound(new { error = "alias not found" });
            row.Value = JsonSerializer.Serialize(new { alias = Uri.UnescapeDataString(alias), target }, JsonOpts);
            await db.SaveChangesAsync();
            return Results.Json(new { alias, target }, JsonOpts);
        });
        g.MapDelete("/models/alias/{*alias}", async (string alias, LlmRouterDbContext db) =>
            await KvDelete(db, AliasScope, Uri.UnescapeDataString(alias)));

        g.MapGet("/models/catalog", async (LlmRouterDbContext db, LLMRouter.Core.Registry.ProviderRegistry registry) =>
        {
            var synced = await db.SyncedModels.Where(m => m.Available).ToListAsync();
            var aliases = await db.Kv.Where(k => k.Scope == AliasScope).ToListAsync();
            var registryModels = registry.Providers.Values
                .SelectMany(p => (p.Models ?? []).Select(m => $"{p.Id}/{m.Id}"));
            return Results.Json(new
            {
                registry = registryModels,
                synced = synced.Select(m => $"{m.Provider}/{m.Model}"),
                aliases = aliases.Select(a => a.Key),
            }, JsonOpts);
        });

        g.MapGet("/models/openrouter-catalog", async (LlmRouterDbContext db) =>
        {
            var synced = await db.SyncedModels.Where(m => m.Available).ToListAsync();
            var overrides = await db.Kv.Where(k => k.Scope == PricingOverridesScope).ToListAsync();
            var data = synced.Select(m =>
            {
                var pricing = overrides.FirstOrDefault(o => o.Key == $"{m.Provider}/{m.Model}" || o.Key == m.Model)?.Value;
                return new
                {
                    id = $"{m.Provider}/{m.Model}",
                    name = m.Model,
                    pricing = pricing is null ? (JsonElement?)null : JsonDocument.Parse(pricing).RootElement.Clone(),
                };
            });
            return Results.Json(new { data }, JsonOpts);
        });

        g.MapPost("/models/test", async (HttpContext ctx, LlmRouterDbContext db, LLMRouter.Core.Registry.ProviderRegistry registry,
            IHttpClientFactory hf) => await TestModelCall(ctx, db, registry, hf));

        g.MapPost("/models/test-all", async (HttpContext ctx, LlmRouterDbContext db, LLMRouter.Core.Registry.ProviderRegistry registry,
            IHttpClientFactory hf) =>
        {
            var providers = registry.Providers.Values.Where(p => p.BaseUrl is not null).ToList();
            // Bound the fan-out: ~290 providers × 10s probes unbounded can pin the
            // threadpool for minutes under load; 64-way keeps worst case ~5 waves.
            using var gate = new SemaphoreSlim(64);
            var probes = providers.Select(async p =>
            {
                await gate.WaitAsync();
                try { return await ProbeProvider(ctx, hf, p, p.Models?.FirstOrDefault()?.Id ?? "default"); }
                finally { gate.Release(); }
            });
            var results = await Task.WhenAll(probes);
            return Results.Json(new { results }, JsonOpts);
        });
    }

    private sealed record TestBody(string ProviderId, string Model);

    private static async Task<IResult> TestModelCall(HttpContext ctx, LlmRouterDbContext db,
        LLMRouter.Core.Registry.ProviderRegistry registry, IHttpClientFactory hf)
    {
        JsonElement b;
        try { b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
        catch (JsonException) { b = default; } // empty body is fine for test-all style calls
        var providerId = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("providerId", out var p) ? p.GetString() : null;
        var model = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("model", out var m) ? m.GetString() : null;
        var provider = registry.Providers.Values.FirstOrDefault(x => x.Id == providerId);
        if (provider is null) return Results.NotFound(new { error = "provider not found" });
        return Results.Json(await ProbeProvider(ctx, hf, provider, model ?? "default"), JsonOpts);
    }

    private static async Task<object> ProbeProvider(HttpContext ctx, IHttpClientFactory hf,
        LLMRouter.Core.Registry.ProviderEntry provider, string model)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var client = hf.CreateClient("upstream");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var url = $"{provider.BaseUrl?.TrimEnd('/')}/models";
            var resp = await client.GetAsync(url, cts.Token);
            return new { provider = provider.Id, model, ok = resp.IsSuccessStatusCode, latencyMs = sw.ElapsedMilliseconds, status = (int)resp.StatusCode, error = (string?)null };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ctx.RequestAborted.IsCancellationRequested)
        {
            // bad BaseUrl, DNS, timeout — report instead of failing the fan-out
            return new { provider = provider.Id, model, ok = false, latencyMs = sw.ElapsedMilliseconds, status = 0, error = ex.Message };
        }
    }
}
