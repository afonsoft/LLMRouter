using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-068: provider ops — bulk key import, credential-shape validation,
/// and concurrent connection test batch. Mirrors OmniRoute's provider ops
/// (providers/bulk, providers/validate, providers/test-batch).
/// </summary>
public static class ProviderOpsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const int MaxBulkKeys = 200;
    private const int MaxBatchIds = 100;
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(30);
    private const int MaxParallelTests = 8;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- bulk import: create many key-connections for one provider in a
        // single call. Per-entry results, always 200 once the provider resolves.
        g.MapPost("/providers/bulk", async (HttpContext ctx, LlmRouterDbContext db, ProviderRegistry r) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var provider = Get(b, "provider");
            if (provider is null)
                return Results.BadRequest(new { error = "provider is required" });
            var p = r.GetProvider(provider)
                ?? (NodeResolver.IsNodeProviderId(provider) ? await NodeResolver.ResolveAsync(db, provider) : null);
            if (p is null)
                return Results.BadRequest(new { error = $"unknown provider: {provider}" });

            if (!b.TryGetProperty("keys", out var keysEl) || keysEl.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "keys must be an array of strings" });
            var rawKeys = keysEl.EnumerateArray()
                .Select(k => k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "")
                .ToList();
            if (rawKeys.Count == 0)
                return Results.BadRequest(new { error = "keys must not be empty" });
            if (rawKeys.Count > MaxBulkKeys)
                return Results.BadRequest(new { error = $"at most {MaxBulkKeys} keys per call" });

            var namePrefix = Get(b, "namePrefix") ?? provider;
            var baseUrl = Get(b, "baseUrl");
            var authType = Get(b, "authType") ?? p.AuthType;

            var existing = await db.ProviderConnections
                .Where(c => c.Provider == provider)
                .ToListAsync();
            var existingSecrets = existing
                .Select(GatewayEngine.ConnectionSecret)
                .Where(s => s is not null)
                .ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var results = new List<object>();
            var created = 0;
            for (var i = 0; i < rawKeys.Count; i++)
            {
                var key = rawKeys[i].Trim();
                var masked = MaskKey(key);
                var err = ValidateKeyShape(key, p);
                if (err is not null)
                {
                    results.Add(new { index = i, ok = false, masked, error = err });
                    continue;
                }
                if (!seen.Add(key) || existingSecrets.Contains(key))
                {
                    results.Add(new { index = i, ok = false, masked, error = "duplicate key" });
                    continue;
                }
                var data = new Dictionary<string, string> { ["apiKey"] = key };
                if (baseUrl is not null) data["baseUrl"] = baseUrl;
                var c = new ProviderConnection
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Provider = provider,
                    AuthType = authType,
                    Name = $"{namePrefix}-{i + 1}",
                    IsActive = true,
                    Data = JsonSerializer.Serialize(data),
                    CreatedAt = Now(), UpdatedAt = Now(),
                };
                db.ProviderConnections.Add(c);
                existingSecrets.Add(key);
                created++;
                results.Add(new { index = i, ok = true, masked, id = c.Id, name = c.Name });
            }
            await db.SaveChangesAsync();
            if (created > 0)
                await Core.Extras.Extras.AuditAsync(db, "connection.bulk_create", $"{provider}: {created}/{rawKeys.Count}");
            return Results.Json(new { created, total = rawKeys.Count, results }, JsonOpts);
        });

        // ---- validate: credential shape check + optional live ping (`live:true`)
        // hits the provider's models endpoint with the candidate key.
        g.MapPost("/providers/validate", async (HttpContext ctx, LlmRouterDbContext db, ProviderRegistry r, IHttpClientFactory hf, CancellationToken ct) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var provider = Get(b, "provider");
            if (provider is null)
                return Results.BadRequest(new { error = "provider is required" });
            var p = r.GetProvider(provider)
                ?? (NodeResolver.IsNodeProviderId(provider) ? await NodeResolver.ResolveAsync(db, provider) : null);
            if (p is null)
                return Results.BadRequest(new { error = $"unknown provider: {provider}" });
            if (!b.TryGetProperty("key", out var keyEl))
                return Results.BadRequest(new { error = "key is required" });
            var rawKey = keyEl.ValueKind == JsonValueKind.String ? keyEl.GetString() ?? "" : "";

            var errors = new List<string>();
            var warnings = new List<string>();
            var key = rawKey.Trim();
            if (key.Length != rawKey.Length)
                warnings.Add("leading/trailing whitespace was trimmed");
            if (key.Length == 0)
            {
                if (p.AuthType is "none" or "optional")
                    warnings.Add($"provider {provider} does not require a credential ({p.AuthType})");
                else
                    errors.Add("key is empty");
            }
            else
            {
                if (key.Any(char.IsWhiteSpace)) errors.Add("key contains whitespace");
                if (key.Any(char.IsControl)) errors.Add("key contains control characters");
                if (key.Length < 6) errors.Add("key is too short");
                if (key.Length > 4096) errors.Add("key is too long");
                if (key.Length is >= 6 and < 12) warnings.Add("key is unusually short");
            }
            var valid = errors.Count == 0;

            object? live = null;
            if (valid && b.TryGetProperty("live", out var lv) && lv.ValueKind == JsonValueKind.True)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(PingTimeout);
                var t = await PingAsync(r, p, Get(b, "baseUrl"), key, hf, cts.Token);
                live = new { ok = t.Status == "ok", status = t.Status, httpStatus = t.HttpStatus, latencyMs = t.LatencyMs, error = t.Error };
            }
            return Results.Json(new { valid, errors, warnings, live }, JsonOpts);
        });

        // ---- test-batch: concurrent re-test of a set of connections.
        // Each connection gets {id, ok, latencyMs, error}; unknown ids report
        // ok=false + "not found". Test outcomes persist onto connection.Data.
        g.MapPost("/providers/test-batch", async (HttpContext ctx, LlmRouterDbContext db, ProviderRegistry r, IHttpClientFactory hf, CancellationToken ct) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (!b.TryGetProperty("ids", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "ids must be an array of strings" });
            var ids = idsEl.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "")
                .Where(x => x.Length > 0)
                .Distinct()
                .ToList();
            if (ids.Count == 0)
                return Results.BadRequest(new { error = "ids must not be empty" });
            if (ids.Count > MaxBatchIds)
                return Results.BadRequest(new { error = $"at most {MaxBatchIds} ids per call" });

            var conns = await db.ProviderConnections.Where(c => ids.Contains(c.Id)).ToListAsync();
            var byId = conns.ToDictionary(c => c.Id);
            // resolve providers sequentially (DbContext is not thread-safe)
            var providers = new Dictionary<string, ProviderEntry?>();
            foreach (var c in conns)
                if (!providers.ContainsKey(c.Provider))
                    providers[c.Provider] = r.GetProvider(c.Provider) ?? await NodeResolver.ResolveAsync(db, c.Provider);

            var probes = new Dictionary<string, Task<(string Status, int HttpStatus, long LatencyMs, string? Error)>>();
            using var gate = new SemaphoreSlim(MaxParallelTests);
            foreach (var c in conns)
            {
                providers.TryGetValue(c.Provider, out var p);
                if (p is null)
                {
                    probes[c.Id] = Task.FromResult(("error", 0, 0L, (string?)"unknown provider"));
                    continue;
                }
                string? connBase = null;
                try
                {
                    if (JsonDocument.Parse(c.Data).RootElement.TryGetProperty("baseUrl", out var bu))
                        connBase = bu.GetString();
                }
                catch { /* best-effort: failure is non-fatal */ }
                var secret = GatewayEngine.ConnectionSecret(c);
                probes[c.Id] = Task.Run(async () =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(PingTimeout);
                        return await PingAsync(r, p, connBase, secret, hf, cts.Token);
                    }
                    finally { gate.Release(); }
                }, ct);
            }
            await Task.WhenAll(probes.Values);

            var results = new List<object>();
            foreach (var id in ids)
            {
                if (!byId.TryGetValue(id, out var c))
                {
                    results.Add(new { id, ok = false, latencyMs = 0L, error = "not found" });
                    continue;
                }
                var t = await probes[id];
                var ok = t.Status == "ok";
                PersistTestResult(c, t.Status, t.LatencyMs, t.Error);
                results.Add(new { id, ok, status = t.Status, httpStatus = t.HttpStatus, latencyMs = t.LatencyMs, error = t.Error });
            }
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "connection.test_batch", $"{conns.Count}/{ids.Count} tested");
            return Results.Json(new { results, tested = conns.Count, total = ids.Count }, JsonOpts);
        });

        // ---- SPEC-075: anthropic API-key rate-limit quota probe
        // (open-sse/services/usage/anthropicApiKey.ts). Anthropic exposes no
        // usage endpoint to a plain key, but every /v1/messages response
        // carries the per-minute windows in headers — one minimal request reads them.
        g.MapGet("/provider-connections/{id}/usage-quota", async (HttpContext ctx, string id,
            LlmRouterDbContext db, ProviderRegistry r, IHttpClientFactory hf, CancellationToken ct) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "connection not found" });
            var p = r.GetProvider(c.Provider);
            var apiKey = GatewayEngine.ConnectionSecret(c);
            if (apiKey is not { Length: > 0 })
                return Results.Json(new { message = "API key not available on this connection." });

            var baseUrl = (p is not null ? GatewayEngine.ConnectionBaseUrl(c, p) : null)
                ?? "https://api.anthropic.com";
            var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/v1/messages")
            {
                Content = JsonContent.Create(new
                {
                    model = "claude-haiku-4-5-20251001",
                    max_tokens = 1,
                    messages = new[] { new { role = "user", content = "hi" } },
                }),
            };
            req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            // SPEC-077: connection customHeaders on the quota probe as well
            var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Core.Gateway.CustomHeaders.Apply(custom, c.Data);
            foreach (var kv in custom) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            HttpResponseMessage resp;
            try { resp = await hf.CreateClient("upstream").SendAsync(req, ct); }
            catch { return Results.Json(new { message = "Anthropic usage request failed." }); }
            if (!resp.IsSuccessStatusCode)
                return Results.Json(new { message = $"Anthropic usage request returned {(int)resp.StatusCode}" });

            var quotas = AnthropicQuotaProbe.ReadWindows(resp);
            return quotas.Count == 0
                ? Results.Json(new { message = "Anthropic response carried no rate-limit headers." })
                : Results.Json(new { plan = "API key", quotas });
        });

        // ==================== SPEC-054 remainder ====================

        // ---- health-matrix: connections × last-N checks (usage rows + persisted test)
        g.MapGet("/providers/health-matrix", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var window = int.TryParse(ctx.Request.Query["n"], out var n) ? Math.Clamp(n, 1, 50) : 10;
            var conns = await db.ProviderConnections.OrderBy(c => c.Provider).ThenBy(c => c.Name).ToListAsync();
            var usage = await db.UsageHistory
                .Where(u => u.ConnectionId != null)
                .OrderByDescending(u => u.Timestamp)
                .Take(conns.Count * window)
                .ToListAsync();
            var connections = conns.Select(c =>
            {
                var d = Core.ProviderOps.ProviderRules.DataOf(c);
                var checks = usage.Where(u => u.ConnectionId == c.Id).Take(window)
                    .Select(u => (at: u.Timestamp, status: u.Status ?? "", latencyMs: u.LatencyMs))
                    .ToList();
                if (d.TryGetValue("lastTestAt", out var lt) && lt.ValueKind == JsonValueKind.String)
                    checks.Add((lt.GetString() ?? "",
                        d.TryGetValue("testStatus", out var ts) ? ts.GetString() ?? "?" : "?",
                        d.TryGetValue("latencyMs", out var lm) && lm.TryGetInt64(out var l) ? l : 0));
                checks = checks.OrderByDescending(x => x.at).Take(window).ToList();
                var okCount = checks.Count(x => x.status is "ok" or "success" or "200");
                var healthy = checks.Count > 0 && okCount * 2 >= checks.Count;
                return new
                {
                    c.Id, c.Provider, c.Name, c.IsActive,
                    checks = checks.Select(x => new { x.at, x.status, x.latencyMs }),
                    healthy,
                };
            });
            return Results.Json(new { window, connections }, JsonOpts);
        });

        // ---- health-autopilot: settings + manual actions; the sweep disables
        // connections whose recent-check failure rate meets the threshold and
        // re-tests/re-enables ones that recover. Stored on settings row data.
        const string AutoKey = "healthAutopilot";
        g.MapGet("/providers/health-autopilot", async (LlmRouterDbContext db) =>
            Results.Json(await AutopilotCfgAsync(db, AutoKey), JsonOpts));

        g.MapPut("/providers/health-autopilot", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var cfg = await AutopilotCfgAsync(db, AutoKey);
            cfg["enabled"] = JsonSerializer.SerializeToElement(
                b.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True);
            if (b.TryGetProperty("failureThreshold", out var ft) && ft.TryGetDouble(out var f))
                cfg["failureThreshold"] = JsonSerializer.SerializeToElement(Math.Clamp(f, 0.05, 1.0));
            if (b.TryGetProperty("minChecks", out var mc) && mc.TryGetInt32(out var m))
                cfg["minChecks"] = JsonSerializer.SerializeToElement(Math.Clamp(m, 1, 100));
            if (b.TryGetProperty("retestMin", out var rm) && rm.TryGetInt32(out var r))
                cfg["retestMin"] = JsonSerializer.SerializeToElement(Math.Clamp(r, 1, 1440));
            await SaveAutopilotAsync(db, AutoKey, cfg);
            return Results.Json(cfg, JsonOpts);
        });

        g.MapPost("/providers/health-autopilot/actions", async (HttpContext ctx, LlmRouterDbContext db, ProviderRegistry r, IHttpClientFactory hf, CancellationToken ct) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var action = Get(b, "action") ?? "sweep";
            var cfg = await AutopilotCfgAsync(db, AutoKey);
            switch (action)
            {
                case "sweep":
                {
                    var result = await AutopilotSweepAsync(db, cfg);
                    await Core.Extras.Extras.AuditAsync(db, "providers.autopilot_sweep",
                        $"disabled {result.Disabled.Count}, reenabled {result.Reenabled.Count}");
                    return Results.Json(result, JsonOpts);
                }
                case "enable-all":
                {
                    var off = await db.ProviderConnections.Where(c => !c.IsActive).ToListAsync();
                    foreach (var c in off) { c.IsActive = true; c.UpdatedAt = Now(); }
                    await db.SaveChangesAsync();
                    await Core.Extras.Extras.AuditAsync(db, "providers.autopilot_enable_all", $"{off.Count}");
                    return Results.Json(new { reenabled = off.Select(c => c.Id) }, JsonOpts);
                }
                case "retest":
                {
                    // re-test currently-disabled connections; re-enable those that pass
                    var off = await db.ProviderConnections.Where(c => !c.IsActive).ToListAsync();
                    var reenabled = new List<string>();
                    var providers = new Dictionary<string, ProviderEntry?>();
                    foreach (var c in off)
                    {
                        if (!providers.TryGetValue(c.Provider, out var p))
                            providers[c.Provider] = p = r.GetProvider(c.Provider) ?? await NodeResolver.ResolveAsync(db, c.Provider, ct);
                        if (p is null) continue;
                        string? connBase = null;
                        if (JsonDocument.Parse(c.Data).RootElement.TryGetProperty("baseUrl", out var bu)) connBase = bu.GetString();
                        var t = await PingAsync(r, p, connBase, GatewayEngine.ConnectionSecret(c), hf, ct);
                        PersistTestResult(c, t.Status, t.LatencyMs, t.Error);
                        if (t.Status == "ok") { c.IsActive = true; reenabled.Add(c.Id); }
                    }
                    await db.SaveChangesAsync();
                    await Core.Extras.Extras.AuditAsync(db, "providers.autopilot_retest", $"{reenabled.Count}/{off.Count}");
                    return Results.Json(new { reenabled, tested = off.Count }, JsonOpts);
                }
                default:
                    return Results.BadRequest(new { error = $"unknown action: {action}" });
            }
        });

        // ---- deprecated / expiration listings
        g.MapGet("/providers/deprecated", async (LlmRouterDbContext db) =>
        {
            var conns = await db.ProviderConnections.ToListAsync();
            var deprecated = conns.Where(c =>
                    Core.ProviderOps.ProviderRules.DataOf(c)
                        .TryGetValue("deprecated", out var d) && d.ValueKind == JsonValueKind.True)
                .Select(c => new { c.Id, c.Provider, c.Name, c.IsActive })
                .ToList();
            var models = conns.SelectMany(c =>
                    Core.ProviderOps.ProviderRules.DataOf(c)
                        .TryGetValue("models", out var ms) && ms.ValueKind == JsonValueKind.Array
                        ? ms.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object
                            && m.TryGetProperty("deprecated", out var dd) && dd.ValueKind == JsonValueKind.True)
                            .Select(m => new { connectionId = c.Id, provider = c.Provider, model = m.TryGetProperty("id", out var i) ? i.GetString() : "?" })
                        : [])
                .ToList();
            return Results.Json(new { connections = deprecated, models }, JsonOpts);
        });

        g.MapGet("/providers/expiration", async (LlmRouterDbContext db) =>
        {
            var conns = await db.ProviderConnections.ToListAsync();
            var now = DateTime.UtcNow;
            var rows = conns.Select(c =>
                {
                    var d = Core.ProviderOps.ProviderRules.DataOf(c);
                    return d.TryGetValue("expiresAt", out var ex) && ex.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(ex.GetString(), out var at)
                        ? new { c.Id, c.Provider, c.Name, expiresAt = at }
                        : null;
                })
                .Where(x => x is not null)
                .Select(x => new { x!.Id, x.Provider, x.Name, expiresAt = x.expiresAt.ToString("o"), daysLeft = (int)(x.expiresAt - now).TotalDays })
                .OrderBy(x => x.daysLeft)
                .ToList();
            return Results.Json(new
            {
                expired = rows.Where(x => x.daysLeft < 0),
                expiringSoon = rows.Where(x => x.daysLeft is >= 0 and <= 30),
                tracked = rows.Count,
            }, JsonOpts);
        });

        // ---- free-onboarding: providers/connections usable without a paid key
        g.MapGet("/providers/free-onboarding", (LlmRouterDbContext db, ProviderRegistry r) =>
        {
            var freeProviders = r.Providers.Values
                .Where(p => p.AuthType is "none" or "optional")
                .Select(p => new { p.Id, p.Alias, p.AuthType, p.BaseUrl })
                .ToList();
            var conns = db.ProviderConnections.Where(c => c.IsActive).ToList()
                .Where(c => Core.ProviderOps.ProviderRules.DataOf(c)
                    .TryGetValue("freeOnboarding", out var f) && f.ValueKind == JsonValueKind.True)
                .Select(c => new { c.Id, c.Provider, c.Name })
                .ToList();
            return Results.Json(new { providers = freeProviders, connections = conns }, JsonOpts);
        });

        // ---- openrouter-stats: per-connection usage aggregates (upstream shows
        // openrouter-specific stats; ours aggregates any provider's usage rows)
        g.MapGet("/providers/openrouter-stats", async (LlmRouterDbContext db) =>
        {
            var conns = await db.ProviderConnections
                .Where(c => c.Provider.Contains("openrouter")).ToListAsync();
            var usage = await db.UsageHistory.Where(u => u.ConnectionId != null).ToListAsync();
            var stats = conns.Select(c =>
            {
                var rows = usage.Where(u => u.ConnectionId == c.Id).ToList();
                return new
                {
                    c.Id, c.Name, c.IsActive,
                    requests = rows.Count,
                    promptTokens = rows.Sum(u => u.PromptTokens),
                    completionTokens = rows.Sum(u => u.CompletionTokens),
                    cost = Math.Round(rows.Sum(u => u.Cost), 6),
                    errors = rows.Count(u => u.Status is not ("200" or "ok" or "success")),
                };
            });
            return Results.Json(new { connections = stats }, JsonOpts);
        });

        // ---- web-session contract + bulk-web-session (subscription/web-auth conns)
        const string ContractKey = "webSessionContract";
        g.MapGet("/providers/web-session-contract", async (LlmRouterDbContext db) =>
            Results.Json(await WebContractAsync(db, ContractKey), JsonOpts));

        g.MapPut("/providers/web-session-contract", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "contract must be a JSON object" });
            await UpsertKvAsync(db, "settings", ContractKey, b.GetRawText());
            return Results.Json(b, JsonOpts);
        });

        g.MapPost("/providers/bulk-web-session", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var provider = Get(b, "provider");
            if (provider is null) return Results.BadRequest(new { error = "provider is required" });
            var contract = await WebContractAsync(db, ContractKey);
            var required = contract.TryGetValue("requiredFields", out var rf) && rf.ValueKind == JsonValueKind.Array
                ? rf.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : new List<string>();
            if (!b.TryGetProperty("sessions", out var sess) || sess.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "sessions must be an array" });
            var results = new List<object>();
            var created = 0;
            var i = 0;
            foreach (var s in sess.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object)
                { results.Add(new { index = i, ok = false, error = "session must be an object" }); i++; continue; }
                var missing = required.Where(f =>
                    !s.TryGetProperty(f, out var v) || v.ValueKind != JsonValueKind.String || v.GetString() is not { Length: > 0 }).ToList();
                if (missing.Count > 0)
                { results.Add(new { index = i, ok = false, error = $"missing fields: {string.Join(',', missing)}" }); i++; continue; }
                var c = new ProviderConnection
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Provider = provider, AuthType = "web",
                    Name = s.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                        ? nm.GetString()! : $"{provider}-web-{i + 1}",
                    IsActive = true,
                    Data = JsonSerializer.Serialize(new Dictionary<string, JsonElement>
                    {
                        ["session"] = JsonSerializer.SerializeToElement(s),
                    }),
                    CreatedAt = Now(), UpdatedAt = Now(),
                };
                db.ProviderConnections.Add(c);
                created++;
                results.Add(new { index = i, ok = true, id = c.Id, name = c.Name });
                i++;
            }
            await db.SaveChangesAsync();
            if (created > 0)
                await Core.Extras.Extras.AuditAsync(db, "connection.bulk_web_session", $"{provider}: {created}");
            return Results.Json(new { created, total = results.Count, results }, JsonOpts);
        });

        // ---- per-connection ops: interception-rules / param-filters / cc-alias /
        // sync-models / refresh-cursor / chatgpt-web-codex-doctor — all stored
        // inside providerConnection.Data.
        g.MapGet("/provider-connections/{id}/interception-rules", async (LlmRouterDbContext db, string id) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "connection not found" });
            var d = Core.ProviderOps.ProviderRules.DataOf(c);
            return Results.Json(new { rules = d.TryGetValue("interceptionRules", out var r) ? r : JsonDocument.Parse("[]").RootElement }, JsonOpts);
        });

        g.MapPut("/provider-connections/{id}/interception-rules", async (HttpContext ctx, LlmRouterDbContext db, string id) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "connection not found" });
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "rules must be an array" });
            Core.ProviderOps.ProviderRules.SetData(c, "interceptionRules", b);
            await db.SaveChangesAsync();
            return Results.Json(new { rules = b }, JsonOpts);
        });

        g.MapGet("/provider-connections/{id}/param-filters", async (LlmRouterDbContext db, string id) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "connection not found" });
            var d = Core.ProviderOps.ProviderRules.DataOf(c);
            return Results.Json(new { filters = d.TryGetValue("paramFilters", out var f) ? f : JsonDocument.Parse("[]").RootElement }, JsonOpts);
        });

        g.MapPut("/provider-connections/{id}/param-filters", async (HttpContext ctx, LlmRouterDbContext db, string id) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "connection not found" });
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "filters must be an array of param names" });
            Core.ProviderOps.ProviderRules.SetData(c, "paramFilters", b);
            await db.SaveChangesAsync();
            return Results.Json(new { filters = b }, JsonOpts);
        });

        g.MapGet("/provider-connections/{id}/cc-alias", async (LlmRouterDbContext db, string id) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "connection not found" });
            var d = Core.ProviderOps.ProviderRules.DataOf(c);
            return Results.Json(new { aliases = d.TryGetValue("ccAlias", out var a) ? a : JsonDocument.Parse("{}").RootElement }, JsonOpts);
        });

        g.MapPut("/provider-connections/{id}/cc-alias", async (HttpContext ctx, LlmRouterDbContext db, string id) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "connection not found" });
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "aliases must be an object {alias: model}" });
            Core.ProviderOps.ProviderRules.SetData(c, "ccAlias", b);
            await db.SaveChangesAsync();
            return Results.Json(new { aliases = b }, JsonOpts);
        });

        // sync-models: fetch {baseUrl}/models with conn auth → Data.syncedModels
        g.MapPost("/provider-connections/{id}/sync-models", async (HttpContext ctx, string id,
            LlmRouterDbContext db, ProviderRegistry r, IHttpClientFactory hf, CancellationToken ct) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "connection not found" });
            var p = r.GetProvider(c.Provider) ?? await NodeResolver.ResolveAsync(db, c.Provider, ct);
            if (p is null) return Results.BadRequest(new { error = "unknown provider" });
            var baseUrl = GatewayEngine.ConnectionBaseUrl(c, p).TrimEnd('/');
            var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
            var secret = GatewayEngine.ConnectionSecret(c);
            if (secret is { Length: > 0 })
            {
                var ah = new Dictionary<string, string>();
                GatewayEngine.ApplyAuth(ah, p, secret);
                foreach (var kv in ah) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
            HttpResponseMessage resp;
            try { resp = await hf.CreateClient("upstream").SendAsync(req, ct); }
            catch (Exception ex) { return Results.Json(new { error = $"upstream request failed: {ex.Message}" }); }
            if (!resp.IsSuccessStatusCode)
                return Results.Json(new { error = $"upstream returned {(int)resp.StatusCode}" });
            var el = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
            var models = el.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray()
                    .Select(m => m.TryGetProperty("id", out var i2) ? i2.GetString() : null)
                    .Where(m => m is { Length: > 0 }).Cast<string>().ToList()
                : [];
            Core.ProviderOps.ProviderRules.SetData(c, "syncedModels", JsonSerializer.SerializeToElement(models));
            Core.ProviderOps.ProviderRules.SetData(c, "syncedModelsAt", JsonSerializer.SerializeToElement(Now()));
            await db.SaveChangesAsync();
            return Results.Json(new { count = models.Count, models }, JsonOpts);
        });

        // refresh-cursor: reset the incremental sync cursor so next sync refetches all
        g.MapPost("/provider-connections/{id}/refresh-cursor", async (LlmRouterDbContext db, string id) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "connection not found" });
            var d = Core.ProviderOps.ProviderRules.DataOf(c);
            d.Remove("syncCursor");
            d.Remove("syncedModels");
            c.Data = JsonSerializer.Serialize(d);
            c.UpdatedAt = Now();
            await db.SaveChangesAsync();
            return Results.Json(new { cleared = true }, JsonOpts);
        });

        // chatgpt-web-codex-doctor: structural diagnostics over a web/codex conn
        g.MapPost("/provider-connections/{id}/chatgpt-web-codex-doctor", async (LlmRouterDbContext db, string id) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "connection not found" });
            var d = Core.ProviderOps.ProviderRules.DataOf(c);
            var checks = new List<object>();
            void Check(string name, bool ok, string detail) => checks.Add(new { name, ok, detail });
            var hasSession = d.TryGetValue("session", out var sess) && sess.ValueKind == JsonValueKind.Object;
            Check("session-payload", hasSession, hasSession ? "session object present" : "no session object in connection data");
            if (hasSession)
            {
                var hasToken = sess.EnumerateObject().Any(kv =>
                    kv.Name.Contains("token", StringComparison.OrdinalIgnoreCase)
                    || kv.Name.Contains("cookie", StringComparison.OrdinalIgnoreCase));
                Check("session-credential", hasToken, hasToken ? "token/cookie field present" : "no token/cookie field");
                var exp = sess.EnumerateObject().FirstOrDefault(kv => kv.Name.Contains("expir", StringComparison.OrdinalIgnoreCase));
                if (exp.Value.ValueKind == JsonValueKind.String && DateTime.TryParse(exp.Value.GetString(), out var at))
                    Check("session-expiry", at > DateTime.UtcNow, $"expires {at:o}");
                else
                    Check("session-expiry", true, "no expiry field");
            }
            Check("base-url", !d.ContainsKey("baseUrl") ||
                (d["baseUrl"].ValueKind == JsonValueKind.String && Uri.TryCreate(d["baseUrl"].GetString(), UriKind.Absolute, out _)),
                "baseUrl is a valid absolute URI when present");
            var ok = checks.OfType<dynamic>().All(x => (bool)x.ok);
            return Results.Json(new { ok, checks }, JsonOpts);
        });
    }

    private static async Task<Dictionary<string, JsonElement>> AutopilotCfgAsync(LlmRouterDbContext db, string key)
    {
        var row = await db.Kv.FindAsync("settings", key);
        try
        {
            return row is not null
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Value) ?? []
                : [];
        }
        catch { return []; }
    }

    private static Task SaveAutopilotAsync(LlmRouterDbContext db, string key, Dictionary<string, JsonElement> cfg) =>
        UpsertKvAsync(db, "settings", key, JsonSerializer.Serialize(cfg));

    private static async Task<Dictionary<string, JsonElement>> WebContractAsync(LlmRouterDbContext db, string key)
    {
        var row = await db.Kv.FindAsync("settings", key);
        try
        {
            return row is not null
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Value) ?? []
                : [];
        }
        catch { return []; }
    }

    private static async Task UpsertKvAsync(LlmRouterDbContext db, string scope, string key, string value)
    {
        var row = await db.Kv.FindAsync(scope, key);
        if (row is null) db.Kv.Add(new KvEntry { Scope = scope, Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync();
    }

    private sealed record AutopilotResult(List<string> Disabled, List<string> Reenabled, int Checked);

    private static async Task<AutopilotResult> AutopilotSweepAsync(LlmRouterDbContext db, Dictionary<string, JsonElement> cfg)
    {
        var threshold = cfg.TryGetValue("failureThreshold", out var ft) && ft.TryGetDouble(out var f) ? f : 0.5;
        var minChecks = cfg.TryGetValue("minChecks", out var mc) && mc.TryGetInt32(out var m) ? m : 5;
        var conns = await db.ProviderConnections.ToListAsync();
        var usage = await db.UsageHistory.Where(u => u.ConnectionId != null)
            .OrderByDescending(u => u.Timestamp).Take(conns.Count * minChecks).ToListAsync();
        var disabled = new List<string>();
        var reenabled = new List<string>();
        foreach (var c in conns)
        {
            var checks = usage.Where(u => u.ConnectionId == c.Id).Take(minChecks).ToList();
            if (checks.Count < minChecks) continue;
            var failures = checks.Count(u => u.Status is not ("200" or "ok" or "success"));
            var bad = (double)failures / checks.Count >= threshold;
            if (bad && c.IsActive) { c.IsActive = false; c.UpdatedAt = DateTime.UtcNow.ToString("o"); disabled.Add(c.Id); }
            else if (!bad && !c.IsActive) { c.IsActive = true; c.UpdatedAt = DateTime.UtcNow.ToString("o"); reenabled.Add(c.Id); }
        }
        await db.SaveChangesAsync();
        return new AutopilotResult(disabled, reenabled, usage.Count);
    }

    /// <summary>SPEC-075: parse anthropic-ratelimit-* headers into quota windows
    /// (open-sse/services/usage/anthropicApiKey.ts WINDOWS table).</summary>
    public static class AnthropicQuotaProbe
    {
        private static readonly (string Key, string Header, string Label)[] Windows =
        [
            ("requests", "requests", "Requests"),
            ("input_tokens", "input-tokens", "Input tokens"),
            ("output_tokens", "output-tokens", "Output tokens"),
            ("tokens", "tokens", "Tokens"),
        ];

        public static Dictionary<string, object?> ReadWindows(HttpResponseMessage resp)
        {
            var quotas = new Dictionary<string, object?>();
            foreach (var (key, header, label) in Windows)
            {
                if (!TryHeader(resp, $"anthropic-ratelimit-{header}-limit", out var limitRaw)
                    || !TryHeader(resp, $"anthropic-ratelimit-{header}-remaining", out var remainingRaw)
                    || !double.TryParse(limitRaw, out var limit) || limit <= 0
                    || !double.TryParse(remainingRaw, out var remaining))
                    continue;
                TryHeader(resp, $"anthropic-ratelimit-{header}-reset", out var reset);
                quotas[key] = new Dictionary<string, object?>
                {
                    ["used"] = Math.Max(0, limit - remaining),
                    ["total"] = limit,
                    ["remaining"] = remaining,
                    ["remainingPercentage"] = (int)Math.Round(remaining / limit * 100),
                    ["resetAt"] = reset,
                    ["displayName"] = label,
                };
            }
            return quotas;
        }

        private static bool TryHeader(HttpResponseMessage resp, string name, out string? value)
        {
            value = null;
            if (resp.Headers.TryGetValues(name, out var v)) value = v.FirstOrDefault();
            else if (resp.Content.Headers.TryGetValues(name, out v)) value = v.FirstOrDefault();
            return value is not null;
        }
    }

    /// <summary>Shape-check a candidate credential; null means valid.</summary>
    private static string? ValidateKeyShape(string key, ProviderEntry p)
    {
        if (key.Length == 0)
            return p.AuthType is "none" or "optional" ? null : "key is empty";
        if (key.Any(char.IsWhiteSpace)) return "key contains whitespace";
        if (key.Any(char.IsControl)) return "key contains control characters";
        if (key.Length < 6) return "key is too short";
        if (key.Length > 4096) return "key is too long";
        return null;
    }

    /// <summary>Mask a credential for display in per-entry results.</summary>
    private static string MaskKey(string key) =>
        key.Length <= 8 ? new string('*', Math.Max(4, key.Length))
        : $"{key[..4]}…{key[^4..]}";

    /// <summary>
    /// GET the provider's models URL with the given credential — the same probe
    /// the single-connection test endpoint performs. Optional baseUrl override
    /// (connection-level or caller-provided) wins over the provider default.
    /// </summary>
    private static async Task<(string Status, int HttpStatus, long LatencyMs, string? Error)> PingAsync(
        ProviderRegistry r, ProviderEntry p, string? baseUrlOverride, string? secret,
        IHttpClientFactory hf, CancellationToken ct)
    {
        var baseUrl = (baseUrlOverride ?? p.BaseUrl ?? "https://api.openai.com").TrimEnd('/');
        var rawModels = r.GetModelsUrl(p);
        var modelsUrl = baseUrlOverride is not null
            ? $"{baseUrl}/{(rawModels is { } rp && !rp.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? rp.TrimStart('/') : "v1/models")}"
            : rawModels is { } mu
                ? (mu.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? mu : $"{baseUrl}/{mu.TrimStart('/')}")
                : $"{baseUrl}/v1/models";
        var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
        if (secret is { Length: > 0 })
        {
            var authHeaders = new Dictionary<string, string>();
            GatewayEngine.ApplyAuth(authHeaders, p, secret);
            foreach (var kv in authHeaders) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var resp = await hf.CreateClient("upstream").SendAsync(req, ct);
            var status = resp.IsSuccessStatusCode ? "ok"
                : (int)resp.StatusCode is 401 or 403 ? "auth-fail" : "error";
            var text = await resp.Content.ReadAsStringAsync(ct);
            return (status, (int)resp.StatusCode, sw.ElapsedMilliseconds, text[..Math.Min(2000, text.Length)]);
        }
        catch (Exception ex)
        {
            return ("network", 0, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    /// <summary>Persist a probe outcome on the connection row (same fields the
    /// single-test endpoint writes): testStatus, latencyMs, lastTestAt, lastError.</summary>
    private static void PersistTestResult(ProviderConnection c, string status, long latencyMs, string? errBody)
    {
        var dataEl = AuthEndpoints.Parse(c.Data).Deserialize<Dictionary<string, JsonElement>>() ?? [];
        dataEl["testStatus"] = JsonSerializer.SerializeToElement(status);
        dataEl["latencyMs"] = JsonSerializer.SerializeToElement(latencyMs);
        dataEl["lastTestAt"] = JsonSerializer.SerializeToElement(Now());
        if (errBody is not null)
            dataEl["lastError"] = JsonSerializer.SerializeToElement(errBody[..Math.Min(500, errBody.Length)]);
        c.Data = JsonSerializer.Serialize(dataEl);
        c.UpdatedAt = Now();
    }

    private static string? Get(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}
