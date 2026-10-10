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
