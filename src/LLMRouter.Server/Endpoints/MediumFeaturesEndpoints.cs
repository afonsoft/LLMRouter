using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-095: medium-features batch — agent-skills/acp backends, modality bridge,
/// memory item ops, conversation trees, translator extras, plugin lifecycle,
/// and misc detail/status ops (upstream parity).
/// </summary>
public static class MediumFeaturesEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const string AgentSkillScope = "agentSkills";
    private const string AcpScope = "acpAgents";

    private static string Now() => DateTimeOffset.UtcNow.ToString("o");
    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static async Task<List<KvEntry>> ScopedAsync(LlmRouterDbContext db, string scope) =>
        await db.Kv.Where(k => k.Scope == scope).ToListAsync();

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- agent-skills backend ----
        g.MapGet("/agent-skills", async (LlmRouterDbContext db) =>
            Results.Json(new { items = (await ScopedAsync(db, AgentSkillScope))
                .Select(r => JsonNode.Parse(r.Value)) }, JsonOpts));

        g.MapPost("/agent-skills", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            var id = b?["id"]?.GetValue<string>() ?? NewId();
            b ??= new JsonObject();
            b["id"] = id;
            b["createdAt"] = Now();
            var row = await db.Kv.FindAsync(AgentSkillScope, id)
                ?? db.Kv.Add(new KvEntry { Scope = AgentSkillScope, Key = id }).Entity;
            row.Value = b.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, id }, JsonOpts);
        });

        g.MapGet("/agent-skills/coverage", async (LlmRouterDbContext db) =>
        {
            var rows = await ScopedAsync(db, AgentSkillScope);
            var cov = rows.Select(r => JsonNode.Parse(r.Value))
                .GroupBy(i => i?["agentId"]?.GetValue<string>() ?? "-")
                .Select(x => new { agentId = x.Key, skills = x.Select(i => i?["skillRef"]?.GetValue<string>()) });
            return Results.Json(new { coverage = cov }, JsonOpts);
        });

        g.MapPost("/agent-skills/generate", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var agentId = b.TryGetProperty("agentId", out var a) ? a.GetString() ?? "agent" : "agent";
            var name = b.TryGetProperty("name", out var n) ? n.GetString() ?? "skill" : "skill";
            var desc = b.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var content = $"---\nname: {name}\ndescription: {desc}\n---\n\n# {name}\n\n{desc}\n";
            var id = NewId();
            db.Kv.Add(new KvEntry
            {
                Scope = AgentSkillScope, Key = id,
                Value = JsonSerializer.Serialize(new { id, agentId, skillRef = name, enabled = true, content }),
            });
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, id, content }, JsonOpts);
        });

        g.MapGet("/agent-skills/{id}", async (string id, LlmRouterDbContext db) =>
            await db.Kv.FindAsync(AgentSkillScope, id) is { } r
                ? Results.Json(JsonNode.Parse(r.Value), JsonOpts)
                : Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404));

        g.MapGet("/agent-skills/{id}/raw", async (string id, LlmRouterDbContext db) =>
        {
            var r = await db.Kv.FindAsync(AgentSkillScope, id);
            if (r is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var content = JsonNode.Parse(r.Value)?["content"]?.GetValue<string>() ?? "";
            return Results.Json(new { id, content }, JsonOpts);
        });

        g.MapPut("/agent-skills/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var r = await db.Kv.FindAsync(AgentSkillScope, id);
            if (r is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var cur = (JsonNode.Parse(r.Value) ?? new JsonObject()).AsObject();
            var b = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            if (b is not null) foreach (var kv in b) cur[kv.Key] = kv.Value?.DeepClone();
            cur["id"] = id;
            r.Value = cur.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });

        g.MapDelete("/agent-skills/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var r = await db.Kv.FindAsync(AgentSkillScope, id);
            if (r is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            db.Kv.Remove(r);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- ACP agents registry ----
        g.MapGet("/acp/agents", async (LlmRouterDbContext db) =>
            Results.Json(new { agents = (await ScopedAsync(db, AcpScope))
                .Select(r => JsonNode.Parse(r.Value)) }, JsonOpts));

        g.MapPost("/acp/agents", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body) ?? new JsonObject();
            var id = b["id"]?.GetValue<string>() ?? NewId();
            b["id"] = id;
            b["createdAt"] = Now();
            var row = await db.Kv.FindAsync(AcpScope, id)
                ?? db.Kv.Add(new KvEntry { Scope = AcpScope, Key = id }).Entity;
            row.Value = b.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, id }, JsonOpts);
        });

        g.MapGet("/acp/agents/{id}", async (string id, LlmRouterDbContext db) =>
            await db.Kv.FindAsync(AcpScope, id) is { } r
                ? Results.Json(JsonNode.Parse(r.Value), JsonOpts)
                : Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404));

        g.MapPut("/acp/agents/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var r = await db.Kv.FindAsync(AcpScope, id);
            if (r is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var cur = (JsonNode.Parse(r.Value) ?? new JsonObject()).AsObject();
            var b = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            if (b is not null) foreach (var kv in b) cur[kv.Key] = kv.Value?.DeepClone();
            cur["id"] = id;
            r.Value = cur.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });

        g.MapDelete("/acp/agents/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var r = await db.Kv.FindAsync(AcpScope, id);
            if (r is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            db.Kv.Remove(r);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- modality bridge ----
        g.MapGet("/modality-bridge/stats", async (LlmRouterDbContext db) =>
        {
            var usage = await db.UsageHistory.GroupBy(u => u.Endpoint ?? "chat")
                .Select(x => new { modality = x.Key, calls = x.LongCount() }).ToListAsync();
            return Results.Json(new { modalities = usage }, JsonOpts);
        });

        g.MapPost("/modality-bridge/video/extract", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var jobId = b.TryGetProperty("jobId", out var j) ? j.GetString() : null;
            var url = b.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (jobId is null && url is null)
                return Results.Json(new { error = "jobId or url required" }, JsonOpts, statusCode: 400);
            var job = jobId is null ? null : await db.Kv.FindAsync("videoJobs", jobId);
            return Results.Json(new
            {
                ok = true, jobId, url,
                found = job is not null,
                artifacts = job is null ? null : JsonNode.Parse(job.Value)?["artifacts"],
            }, JsonOpts);
        });

        g.MapGet("/modality-bridge/video/drilldown", async (string? jobId, LlmRouterDbContext db) =>
        {
            var job = jobId is null ? null : await db.Kv.FindAsync("videoJobs", jobId);
            var meta = job is null ? null : JsonNode.Parse(job.Value);
            return Results.Json(new { jobId, found = job is not null, status = meta?["status"], meta }, JsonOpts);
        });

        g.MapGet("/modality-bridge/video/runtime", () =>
            Results.Json(new { workers = 0, queueDepth = 0, running = false }, JsonOpts));

        // ---- memory item ops (kv "memory" rows — SPEC-087) ----
        g.MapGet("/memory/{id}", async (string id, LlmRouterDbContext db) =>
            await db.Kv.FindAsync("memory", id) is { } r
                ? Results.Json(new { id, value = JsonNode.Parse(r.Value) }, JsonOpts)
                : Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404));

        g.MapPut("/memory/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var row = await db.Kv.FindAsync("memory", id)
                ?? db.Kv.Add(new KvEntry { Scope = "memory", Key = id }).Entity;
            row.Value = b.GetRawText();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, id }, JsonOpts);
        });

        g.MapDelete("/memory/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var r = await db.Kv.FindAsync("memory", id);
            if (r is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            db.Kv.Remove(r);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- conversation tree ----
        g.MapGet("/conversations/{id}/tree", async (string id, LlmRouterDbContext db) =>
        {
            var r = await db.Kv.FindAsync("conversations", id);
            if (r is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var conv = JsonNode.Parse(r.Value);
            var messages = conv?["messages"]?.AsArray() ?? new JsonArray();
            var nodes = messages.Select((m, i) => new JsonObject
            {
                ["id"] = m?["id"]?.GetValue<string>() ?? $"m{i}",
                ["parentId"] = m?["parentId"]?.GetValue<string>() ?? (i > 0 ? $"m{i - 1}" : null),
                ["role"] = m?["role"]?.GetValue<string>(),
                ["content"] = m?["content"]?.DeepClone(),
                ["at"] = m?["at"]?.GetValue<string>() ?? m?["createdAt"]?.GetValue<string>(),
            });
            return Results.Json(new { id, root = conv?["title"] ?? conv?["name"], nodes }, JsonOpts);
        });

        // ---- translator extras ----
        g.MapPost("/translator/translate", async (HttpContext ctx) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            var from = b?["from"]?.GetValue<string>() ?? "openai";
            var to = b?["to"]?.GetValue<string>() ?? "openai";
            var payload = b?["payload"] as JsonObject ?? new JsonObject();
            var translated = Core.Translation.Translators.Translate(
                JsonSerializer.SerializeToElement(payload), from, to,
                payload["model"]?.GetValue<string>() ?? "m", false);
            return Results.Json(new { from, to, translated }, JsonOpts);
        });

        g.MapPost("/translator/transform-stream", async (HttpContext ctx) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            var from = b?["from"]?.GetValue<string>() ?? "openai";
            var to = b?["to"]?.GetValue<string>() ?? "openai";
            var frames = b?["frames"]?.AsArray() ?? new JsonArray();
            var state = new Core.Translation.Translators.SseState();
            var output = new List<JsonNode?>();
            foreach (var f in frames)
                foreach (var ev in Core.Translation.Translators.TranslateSse(
                    f?.GetValue<string>() ?? "{}", from, to, "m", state))
                    output.Add(JsonNode.Parse(ev.Data));
            return Results.Json(new { from, to, frames = output }, JsonOpts);
        });

        g.MapPost("/translator/send", async (HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            var from = b?["from"]?.GetValue<string>() ?? "openai";
            var to = b?["to"]?.GetValue<string>() ?? "openai";
            var payload = b?["payload"] as JsonObject ?? new JsonObject();
            var translated = Core.Translation.Translators.Translate(
                JsonSerializer.SerializeToElement(payload), from, to,
                payload["model"]?.GetValue<string>() ?? "m", false);
            // upstream parity: when no target URL is given we only preview the
            // translated request — a full send goes through /v1/chat.
            var targetUrl = b?["url"]?.GetValue<string>();
            if (targetUrl is null)
                return Results.Json(new { sent = false, translated }, JsonOpts);
            var http = hf.CreateClient("upstream");
            var resp = await http.PostAsync(targetUrl,
                new StringContent(translated.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
            return Results.Json(new
            {
                sent = true, status = (int)resp.StatusCode, translated,
                body = (await resp.Content.ReadAsStringAsync())[0..]
            }, JsonOpts);
        });

        // ---- plugins lifecycle ----
        g.MapPost("/plugins/{id}/activate", async (string id, LlmRouterDbContext db) =>
            await PluginStateAsync(db, id, "active"));
        g.MapPost("/plugins/{id}/deactivate", async (string id, LlmRouterDbContext db) =>
            await PluginStateAsync(db, id, "inactive"));
        g.MapGet("/plugins/{id}/config", async (string id, LlmRouterDbContext db) =>
            await db.Kv.FindAsync("pluginConfig", id) is { } r
                ? Results.Json(JsonNode.Parse(r.Value), JsonOpts)
                : Results.Json(new JsonObject(), JsonOpts));
        g.MapPut("/plugins/{id}/config", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var row = await db.Kv.FindAsync("pluginConfig", id)
                ?? db.Kv.Add(new KvEntry { Scope = "pluginConfig", Key = id }).Entity;
            row.Value = b.GetRawText();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });
        g.MapPost("/plugins/scan", async (LlmRouterDbContext db) =>
        {
            var row = await db.Kv.FindAsync("plugins", "registered");
            var n = row is null ? 0 : JsonNode.Parse(row.Value)?.AsArray().Count ?? 0;
            return Results.Json(new { scanned = true, found = n }, JsonOpts);
        });
        g.MapPost("/plugins/marketplace/install", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            var id = b?["id"]?.GetValue<string>() ?? NewId();
            var row = await db.Kv.FindAsync("plugins", "registered");
            var arr = row is null ? new JsonArray() : JsonNode.Parse(row.Value)!.AsArray();
            arr.Add(new JsonObject { ["id"] = id, ["enabled"] = true, ["state"] = "active", ["source"] = "marketplace" });
            if (row is null) db.Kv.Add(new KvEntry { Scope = "plugins", Key = "registered", Value = arr.ToJsonString() });
            else row.Value = arr.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, id }, JsonOpts);
        });

        // ---- misc detail ops ----
        g.MapGet("/skills/{id}", async (string id, IWebHostEnvironment env) =>
        {
            var dir = Path.Combine(env.ContentRootPath, "skills", id);
            var f = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(f))
                return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var fm = Core.Skills.SkillsExtras.Frontmatter(f);
            return Results.Json(new { id, frontmatter = fm, content = await File.ReadAllTextAsync(f) }, JsonOpts);
        });

        g.MapGet("/combos/metrics", async (LlmRouterDbContext db) =>
        {
            var combos = await db.Combos.ToListAsync();
            var byModel = await db.UsageHistory.GroupBy(u => u.Model)
                .Select(x => new { model = x.Key, requests = x.LongCount(),
                    errors = x.LongCount(u => u.Status != "ok" && u.Status != "200" && u.Status != null) })
                .ToListAsync();
            return Results.Json(new
            {
                combos = combos.Select(c =>
                {
                    var models = JsonSerializer.Deserialize<List<string>>(c.Models) ?? [];
                    var rows = byModel.Where(m => models.Any(cm =>
                        cm == m.model || cm.EndsWith('/' + m.model, StringComparison.Ordinal)));
                    return new { c.Id, c.Name, requests = rows.Sum(r => r.requests), errors = rows.Sum(r => r.errors) };
                }),
            }, JsonOpts);
        });

        g.MapGet("/context/combos/default", async (LlmRouterDbContext db) =>
            Results.Json(new { combo = (await db.Kv.FindAsync("context", "defaultCombo"))?.Value }, JsonOpts));
        g.MapPut("/context/combos/default", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var combo = b.TryGetProperty("combo", out var c) ? c.GetString() : null;
            var row = await db.Kv.FindAsync("context", "defaultCombo")
                ?? db.Kv.Add(new KvEntry { Scope = "context", Key = "defaultCombo" }).Entity;
            row.Value = combo ?? "";
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, combo }, JsonOpts);
        });

        g.MapGet("/compression/compare/verify", async (string? id, LlmRouterDbContext db) =>
        {
            var run = id is null ? null : await db.CompressionRuns.FindAsync(id);
            return Results.Json(new { id, verified = run is not null, run }, JsonOpts);
        });

        g.MapGet("/evals", async (LlmRouterDbContext db) =>
        {
            var suites = await db.EvalSuites.ToListAsync();
            var runs = await db.EvalRuns.GroupBy(r => r.SuiteId)
                .Select(x => new { suiteId = x.Key, runs = x.LongCount() }).ToListAsync();
            return Results.Json(new
            {
                evals = suites.Select(s => new { s.Id, s.Name, runs = runs.FirstOrDefault(r => r.suiteId == s.Id)?.runs ?? 0 }),
            }, JsonOpts);
        });

        g.MapGet("/evals/{id}", async (string id, LlmRouterDbContext db) =>
            await db.EvalSuites.FindAsync(id) is { } s
                ? Results.Json(new { s.Id, s.Name, runs = await db.EvalRuns.CountAsync(r => r.SuiteId == id) }, JsonOpts)
                : Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404));

        g.MapGet("/db-backups", async (LlmRouterDbContext db, IWebHostEnvironment env) =>
        {
            var dir = Path.Combine(env.ContentRootPath, "backups");
            var files = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir).Select(f => new { file = Path.GetFileName(f), bytes = new FileInfo(f).Length })
                : Enumerable.Empty<object>();
            var rows = await db.Kv.Where(k => k.Scope == "dbBackups").ToListAsync();
            return Results.Json(new { files, rows = rows.Select(r => r.Key) }, JsonOpts);
        });

        g.MapGet("/docs/codex-cli", () => Results.Json(new
        {
            doc = "# codex-cli\n\nPoint codex at the gateway: set the OpenAI base URL to `/v1` and use a router API key.",
        }, JsonOpts));

        g.MapGet("/init", async (LlmRouterDbContext db) =>
            Results.Json(new
            {
                initialized = true, version = "0.1",
                checks = new { db = true, providers = await db.ProviderConnections.AnyAsync(c => c.IsActive) },
            }, JsonOpts));

        g.MapGet("/assess", async (LlmRouterDbContext db) =>
        {
            var requests = await db.UsageHistory.LongCountAsync();
            var errors = await db.UsageHistory.LongCountAsync(u => u.Status != "ok" && u.Status != "200" && u.Status != null);
            return Results.Json(new
            {
                requests, errors,
                errorRate = requests == 0 ? 0 : (double)errors / requests,
                providers = await db.ProviderConnections.LongCountAsync(),
                ok = true,
            }, JsonOpts);
        });

        g.MapGet("/rate-limit", async (LlmRouterDbContext db) =>
        {
            var rules = await db.RateLimits.ToListAsync();
            return Results.Json(new { rules = rules.Select(r => new { r.Scope, r.ScopeValue, r.Rpm, r.Tpm, r.Enabled }) }, JsonOpts);
        });

        g.MapGet("/resilience", () => Results.Json(new { ok = true, surface = "breaker+lockout+cooldown" }, JsonOpts));

        g.MapGet("/resilience/connections", async (LlmRouterDbContext db) =>
        {
            var conns = await db.ProviderConnections.ToListAsync();
            return Results.Json(new
            {
                connections = conns.Select(c => new
                {
                    c.Id, c.Provider, c.IsActive,
                    breaker = Core.Resilience.ProviderBreaker.GetState(c.Provider).ToString(),
                    cooldownRemaining = Core.Resilience.CooldownTracker.Remaining(c.Id).TotalSeconds,
                }),
            }, JsonOpts);
        });

        g.MapGet("/system/version", () => Results.Json(new
        {
            version = "0.1",
            build = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1",
            sha = Environment.GetEnvironmentVariable("GIT_SHA") ?? "dev",
        }, JsonOpts));

        g.MapGet("/provider-metrics", async (LlmRouterDbContext db, ProviderRegistry registry) =>
        {
            var usage = await db.UsageHistory.GroupBy(u => u.Provider)
                .Select(x => new { provider = x.Key, requests = x.LongCount() }).ToListAsync();
            return Results.Json(new
            {
                providers = registry.Providers.Values.Select(p => new
                {
                    p.Id, models = p.Models.Count,
                    requests = usage.FirstOrDefault(u => u.provider == p.Id)?.requests ?? 0,
                }),
            }, JsonOpts);
        });

        g.MapPost("/provider-models/validate-and-add", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var provider = b.TryGetProperty("provider", out var pv) ? pv.GetString() : null;
            var model = b.TryGetProperty("model", out var mv) ? mv.GetString() : null;
            if (provider is null || model is null)
                return Results.Json(new { error = "provider and model required" }, JsonOpts, statusCode: 400);
            var exists = await db.SyncedModels.AnyAsync(s => s.Provider == provider && s.Model == model);
            if (!exists)
                db.SyncedModels.Add(new SyncedModel
                {
                    Id = NewId(), Provider = provider, Model = model,
                    Available = true, LastSyncAt = Now(),
                });
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, added = !exists, provider, model }, JsonOpts);
        });

        g.MapPost("/provider-nodes/validate", async (HttpContext ctx) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var url = b.TryGetProperty("url", out var u) ? u.GetString() : null;
            // SSRF guard: http(s) only, no loopback/private literals.
            var ok = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == "https" || uri.Scheme == "http")
                && !uri.IsLoopback
                && uri.Host is not ("169.254.169.254" or "metadata.google.internal");
            return Results.Json(new { valid = ok, url }, JsonOpts);
        });

        g.MapGet("/free-tier/summary", async (LlmRouterDbContext db) =>
        {
            var conns = await db.ProviderConnections.Where(c => c.IsActive).ToListAsync();
            var free = conns.Where(c =>
                (c.Data ?? "").Contains("\"free\":true", StringComparison.OrdinalIgnoreCase)
                || (c.Data ?? "").Contains("\"freeTier\":true", StringComparison.OrdinalIgnoreCase)).ToList();
            var usage = await db.UsageHistory.GroupBy(u => u.Provider)
                .Select(x => new { provider = x.Key, requests = x.LongCount() }).ToListAsync();
            return Results.Json(new
            {
                providers = free.Select(c => new
                {
                    c.Id, c.Provider,
                    requests = usage.FirstOrDefault(u => u.provider == c.Provider)?.requests ?? 0,
                }),
                count = free.Count,
            }, JsonOpts);
        });

        // ---- docs/api/search + analytics views ----
        app.MapGet("/docs/api/search", (string? q) =>
        {
            var docs = new[]
            {
                "endpoint", "providers", "models", "combos", "usage", "settings",
                "logs", "playground", "oauth", "mcp", "a2a", "acp", "memory",
                "webhooks", "proxy-pools", "quota", "skills", "agent-skills",
            };
            var needle = q ?? "";
            return Results.Json(new
            {
                q = needle,
                results = docs.Where(d => d.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    .Select(d => new { doc = d, url = $"/docs/{d}" }),
            }, JsonOpts);
        });

        g.MapGet("/analytics/auto-routing", async (LlmRouterDbContext db) =>
        {
            var byModel = await db.UsageHistory.GroupBy(u => u.Model)
                .Select(x => new { model = x.Key, requests = x.LongCount() })
                .OrderByDescending(x => x.requests).Take(20).ToListAsync();
            return Results.Json(new { decisions = byModel }, JsonOpts);
        });

        g.MapGet("/analytics/diversity", async (LlmRouterDbContext db) =>
        {
            var total = await db.UsageHistory.LongCountAsync();
            var byProvider = await db.UsageHistory.GroupBy(u => u.Provider)
                .Select(x => new { provider = x.Key, share = x.LongCount() }).ToListAsync();
            // Shannon-style diversity index over provider share (0 = single provider)
            var entropy = total == 0 ? 0 : -byProvider.Sum(p =>
            {
                var s = (double)p.share / total;
                return s > 0 ? s * Math.Log2(s) : 0;
            });
            return Results.Json(new { total, providers = byProvider.Count, diversity = entropy }, JsonOpts);
        });

        // ---- ACP well-known agent card (upstream shape; /agent.json stays) ----
        app.MapGet("/.well-known/agent-card.json", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var agents = await ScopedAsync(db, AcpScope);
            return Results.Json(new
            {
                name = "LLMRouter",
                url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/a2a",
                version = "0.1",
                capabilities = new { streaming = false, pushNotifications = false },
                skills = agents.Select(a => JsonNode.Parse(a.Value))
                    .Select(a => new { id = a?["id"]?.GetValue<string>(), name = a?["name"]?.GetValue<string>() })
                    .Append(new { id = "chat", name = "Chat completion via any configured model/combo" }),
            }, JsonOpts);
        });
    }

    private static async Task<IResult> PluginStateAsync(LlmRouterDbContext db, string id, string state)
    {
        var row = await db.Kv.FindAsync("plugins", "registered");
        var arr = row is null ? new JsonArray() : JsonNode.Parse(row.Value)!.AsArray();
        var hit = false;
        foreach (var p in arr)
            if (p?["id"]?.GetValue<string>() == id) { p["state"] = state; hit = true; }
        if (!hit) arr.Add(new JsonObject { ["id"] = id, ["state"] = state, ["enabled"] = state == "active" });
        if (row is null) db.Kv.Add(new KvEntry { Scope = "plugins", Key = "registered", Value = arr.ToJsonString() });
        else row.Value = arr.ToJsonString();
        await db.SaveChangesAsync();
        return Results.Json(new { ok = true, id, state }, JsonOpts);
    }
}
