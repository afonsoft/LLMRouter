using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using LLMRouter.Core.Translation;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Gateway;

/// <summary>Outcome of resolving a model string to a concrete upstream request.</summary>
public sealed record ResolvedTarget(
    ProviderEntry Provider,
    ProviderConnection Connection,
    string UpstreamModel,
    string? ComboName);

/// <summary>A translated request ready to send upstream.</summary>
public sealed class UpstreamCall
{
    public required string Url { get; init; }
    public required string Body { get; set; }
    public required Dictionary<string, string> Headers { get; init; }
    public required string OutboundFormat { get; init; }
}

/// <summary>
/// Core gateway engine: resolves a requested model to a provider+connection
/// (direct provider/model, or a combo cascade with capability reordering),
/// builds the upstream call in the provider's format, and records usage.
/// </summary>
public sealed class GatewayEngine(
    LlmRouterDbContext db,
    ProviderRegistry registry,
    ComboPlanner planner,
    ModelResolver resolver,
    Routing.RateLimiter? rateLimiter = null,
    UsageWriter? writer = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Resolve a model name into the ordered list of targets to try
    /// (single target for provider/model, cascade for combos).
    /// </summary>
    public Task<List<ResolvedTarget>> ResolveAsync(
        string model,
        JsonElement? requestBody = null,
        CancellationToken ct = default) =>
        ResolveAsync(model, requestBody, allowChains: true, ct);

    private async Task<List<ResolvedTarget>> ResolveAsync(
        string model,
        JsonElement? requestBody,
        bool allowChains,
        CancellationToken ct)
    {
        var sdata = await HotReads.SettingsDataAsync(db);
        var aliases = LoadAliases(
            sdata.ValueKind == JsonValueKind.Object ? sdata.GetRawText() : "{}");

        // model-combo mappings: upstream-facing model name → combo name (auto-wrap)
        var mapping = await HotReads.ComboMappingAsync(db, model);
        var effectiveModel = mapping ?? model;

        var combo = await HotReads.ComboAsync(db, effectiveModel);
        List<string> models;
        if (Routing.AutoCombos.IsAuto(effectiveModel))
        {
            // SPEC-072: virtual auto/* combo — pool resolved live, no persisted row
            models = await HotReads.AutoPoolAsync(db, registry, effectiveModel);
        }
        else if (combo is not null)
        {
            // SPEC-082: expand step kinds (combo-ref, provider-wildcard, objects)
            var expanded = await Routing.ComboSteps.ExpandAsync(db, registry, combo.Models,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { combo.Name });
            var list = expanded.Models;
            models = await ComboStrategies.OrderAsync(combo.Kind, combo.Name, list,
                db, registry, requestBody, combo.StickyLimit, ct);
            // quota-only steps serve only after the ordered candidates
            models.AddRange(expanded.QuotaOnly.Except(models));
        }
        else
        {
            var (p, m) = resolver.Resolve(model, aliases);
            models = [$"{p}/{m}"];
        }

        if (requestBody is { } body)
        {
            var required = ComboPlanner.DetectRequiredCapabilities(body);
            if (required.Count > 0)
            {
                // SPEC-071: capabilityOverrides replace registry-detected caps
                var overrides = await HotReads.CapabilityOverridesAsync(db);
                models = planner.ReorderByCapabilities(models, required, overrides);
            }
        }

        // SPEC-081: disabled models are skipped in every resolution path
        var disabled = await HotReads.DisabledModelsAsync(db);
        if (disabled.Count > 0)
            models = models.Where(m =>
            {
                var (p, mid) = resolver.Resolve(m, aliases);
                return !Routing.DisabledModels.IsDisabled(disabled, p, mid);
            }).ToList();

        // SPEC-102: request-level routing hints (`provider` object) — combo resolutions
        // only; a bare provider/model call bypasses like upstream single-target calls.
        var hints = Routing.RequestHints.Parse(requestBody);
        if (hints is not null && (combo is not null || Routing.AutoCombos.IsAuto(effectiveModel)))
            models = await Routing.RequestHints.ApplyToModelsAsync(
                models, hints, db, resolver, registry, planner, sdata, requestBody, aliases);

        var targets = new List<ResolvedTarget>();
        foreach (var m in models)
        {
            var (providerId, upstreamModel) = resolver.Resolve(m, aliases);
            var provider = registry.GetProvider(providerId)
                ?? await NodeResolver.ResolveAsync(db, providerId, ct);
            if (provider is null) continue;
            // SPEC-021: provider circuit breaker — skip OPEN providers entirely
            if (!Resilience.ProviderBreaker.CanExecute(provider.Id, provider.AuthType)) continue;
            // SPEC-073: sliding quota windows — provider skipped while a window is saturated
            if (await HotReads.QuotaWindowExceededAsync(db, provider.Id)) continue;
            var conns = await HotReads.ActiveConnsAsync(db, provider.Id);
            foreach (var c in conns.Where(c => !Resilience.CooldownTracker.IsCooling(c.Id)))
            {
                if (await Routing.QuotaTracker.ExhaustedAsync(db, c)) continue;
                // SPEC-021: model lockout — per-model quarantine keeps conn alive
                if (Resilience.ModelLockout.IsLocked(provider.Id, c.Id, upstreamModel)) continue;
                targets.Add(new ResolvedTarget(provider, c, upstreamModel, combo?.Name));
            }
        }

        // SPEC-070: per-model cooldowns — provider/model pairs cooling are skipped
        var cooling = await HotReads.LiveModelCooldownsAsync(db);
        if (cooling.Count > 0)
            targets = targets.Where(t => !cooling.Any(mc =>
                (string.Equals(mc.Provider, t.Provider.Id, StringComparison.OrdinalIgnoreCase)
                )
                && (mc.Model == "*" || string.Equals(mc.Model, t.UpstreamModel, StringComparison.OrdinalIgnoreCase)
                    || string.Equals($"{t.Provider.Id}/{t.UpstreamModel}", mc.Model, StringComparison.OrdinalIgnoreCase))))
                .ToList();

        // SPEC-102: target-level hints — data_collection filter + allow_fallbacks.
        if (hints is not null)
            targets = Routing.RequestHints.ApplyToTargets(targets, hints);

        // SPEC-070: named fallback chains — when the whole candidate list is
        // exhausted, try each active chain's steps (model or combo names)
        if (allowChains && targets.Count == 0)
        {
            var chains = await HotReads.FallbackChainsAsync(db);
            foreach (var chain in chains)
            {
                var steps = JsonSerializer.Deserialize<List<JsonElement>>(chain.Steps) ?? [];
                foreach (var step in steps)
                {
                    var sm = step.ValueKind == JsonValueKind.String ? step.GetString()
                        : step.TryGetProperty("model", out var smv) ? smv.GetString() : null;
                    if (string.IsNullOrEmpty(sm)) continue;
                    targets = await ResolveAsync(sm, requestBody, allowChains: false, ct);
                    if (targets.Count > 0) break;
                }
                if (targets.Count > 0) break;
            }
        }
        return targets;
    }

    /// <summary>Build the upstream HTTP call for a target + inbound request.</summary>
    public UpstreamCall BuildCall(
        ResolvedTarget target, string inboundFormat, JsonElement body, bool stream)
    {
        var provider = target.Provider;
        var outbound = provider.Format;
        var translated = Translators.Translate(body, inboundFormat, outbound, target.UpstreamModel, stream);
        // SPEC-075: bedrock executors require strict role alternation — merge
        // consecutive same-role messages (open-sse/executors/bedrock.ts).
        if (provider.Executor == "bedrock" && translated is JsonObject bj)
            Translation.ExtendedTranslators.MergeBedrockSameRoleMessages(bj);
        var (url, headers) = BuildUrlAndAuth(target);
        return new UpstreamCall
        {
            Url = url,
            Headers = headers,
            Body = translated.ToJsonString(JsonOpts),
            OutboundFormat = outbound,
        };
    }

    /// <summary>Effective base URL for a connection (data.baseUrl override or provider default).</summary>
    public static string ConnectionBaseUrl(ProviderConnection c, ProviderEntry provider)
    {
        var baseUrl = c.Data is { Length: > 0 } d
            ? (JsonDocument.Parse(d).RootElement.TryGetProperty("baseUrl", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString()! : provider.BaseUrl)
            : provider.BaseUrl;
        baseUrl ??= "https://api.openai.com";
        return baseUrl.TrimEnd('/');
    }

    /// <summary>Upstream URL + auth headers from connection secrets.</summary>
    public (string Url, Dictionary<string, string> Headers) BuildUrlAndAuth(ResolvedTarget t)
    {
        var baseUrl = ConnectionBaseUrl(t.Connection, t.Provider);

        var url = t.Provider.Format switch
        {
            "claude" => $"{baseUrl}/v1/messages",
            "gemini" => $"{baseUrl}/v1beta/models/{t.UpstreamModel}:generateContent",
            "responsesApi" => $"{baseUrl}/v1/responses",
            _ => $"{baseUrl}/v1/chat/completions",
        };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
        };
        if (t.Provider.Headers is { } extra)
            foreach (var kv in extra) headers[kv.Key] = kv.Value;

        var secret = ConnectionSecret(t.Connection);
        if (secret is not null) ApplyAuth(headers, t.Provider, secret);
        // SPEC-077: connection-level customHeaders ride every upstream request
        // (e.g. anthropic-workspace-id); auth/hop-by-hop names are denied inside.
        CustomHeaders.Apply(headers, t.Connection.Data);
        return (url, headers);
    }

    /// <summary>
    /// providers.json stores an auth *scheme* in authHeader for most providers
    /// ("bearer", "cookie", "none") rather than a literal header name — map them.
    /// </summary>
    public static void ApplyAuth(Dictionary<string, string> headers, ProviderEntry provider, string secret)
    {
        var h = provider.AuthHeader ?? "authorization";
        var value = provider.AuthPrefix is { Length: > 0 } p ? $"{p}{secret}" : secret;
        switch (h.ToLowerInvariant())
        {
            case "bearer":
                headers["Authorization"] = value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? value : $"Bearer {value}";
                break;
            case "cookie":
                headers["Cookie"] = value;
                break;
            case "none":
                break;
            default:
                if (h.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                    && provider.AuthPrefix is null
                    && !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    value = $"Bearer {value}";
                headers[h] = value;
                break;
        }
    }

    /// <summary>Extract the credential stored on a connection (apiKey / accessToken / token).</summary>
    public static string? ConnectionSecret(ProviderConnection c)
    {
        if (c.Data is not { Length: > 0 } d) return null;
        try
        {
            var el = JsonDocument.Parse(d).RootElement;
            foreach (var k in new[] { "apiKey", "accessToken", "token", "key", "secret" })
                // enc:v1: ciphertext migrado do OmniRoute — decifra com
                // STORAGE_ENCRYPTION_KEY; pula para o próximo campo se falhar.
                if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                    && Security.OmniCrypto.MaybeDecrypt(v.GetString()) is { } s)
                    return s;
            return null;
        }
        catch { /* best-effort: failure is non-fatal */ }
        return null;
    }

    private static Dictionary<string, string> LoadAliases(string settingsData)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var el = JsonDocument.Parse(settingsData).RootElement;
            if (el.TryGetProperty("modelAliases", out var a))
                foreach (var kv in a.EnumerateObject())
                    map[kv.Name] = kv.Value.GetString() ?? "";
        }
        catch { /* best-effort: failure is non-fatal */ }
        return map;
    }

    /// <summary>SPEC-139: parse a combo's {"input":x,"output":y} USD/1M pricing JSON.</summary>
    public static bool PricingJsonParses(string json, out (double input, double output) price)
    {
        price = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object ||
                !e.TryGetProperty("input", out var i) || i.ValueKind != JsonValueKind.Number ||
                !e.TryGetProperty("output", out var o) || o.ValueKind != JsonValueKind.Number)
                return false;
            price = (Math.Max(0, i.GetDouble()), Math.Max(0, o.GetDouble()));
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Record a completed (or failed) request.</summary>
    public async Task LogUsageAsync(
        string provider, string model, string? connectionId, string apiKey,
        string endpoint, int promptTokens, int completionTokens,
        string status, string? error, long latencyMs, JsonElement? detail = null,
        double? reportedCostUsd = null, string? userId = null, string? userKeyId = null,
        ProxyLogInfo? plog = null, string? comboName = null,
        // SPEC-143: request/response payloads for ioLogging (persisted only
        // when settings.observability.ioLogging is on).
        string? ioPrompt = null, string? ioResponse = null)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        var sdata = await HotReads.SettingsDataAsync(db);
        // SPEC-116: correlation id links the proxy_logs journal row to this usage row's Meta.
        var corr = Guid.NewGuid().ToString("N")[..16];
        // SPEC-097: a trusted provider-reported cost wins over the computed price.
        var cost = reportedCostUsd ?? Usage.PricingService.ComputeCost(
            sdata, registry,
            provider, model, promptTokens, completionTokens) ?? 0;

        // SPEC-139: a combo with admin-set pricing charges ITS price (resale),
        // not the upstream cost — wins over both reported and computed cost.
        if (comboName is not null &&
            await db.Combos.FirstOrDefaultAsync(c => c.Name == comboName) is { PricingJson: { } pj } combo &&
            PricingJsonParses(pj, out var pr))
            cost = (promptTokens * pr.input + completionTokens * pr.output) / 1_000_000d;

        // SPEC-101: every logged request feeds the scrapeable meter source.
        GatewayMetrics.RecordRequest(new GatewayMetrics.RequestMetric(
            provider, model, connectionId, apiKey,
            endpoint, promptTokens, completionTokens, cost, latencyMs, status));

        // SPEC-074: usage lands via the writer — cached quota/daily-cap
        // verdicts for this provider+key are now stale.
        HotCache.Default.Invalidate($"qwx:{provider}");
        HotCache.Default.InvalidateTag($"dcap:{apiKey}");

        // SPEC-108: usage writes go through the typed channel — batched by the
        // UsageWriter (200 rows / 500ms) in one transaction off the request path.
        if (writer is not null)
        {
            var detailText = ProxyJournal.MergeCorrelation(detail?.GetRawText() ?? error, corr);
            await writer.PostUsageAsync(new UsageWrite(
                now, provider, model, connectionId, apiKey, endpoint,
                promptTokens, completionTokens, cost, latencyMs, status, detailText,
                userId, userKeyId, plog, corr, ioPrompt, ioResponse));

            if (rateLimiter is not null)
            {
                var rlRules = await HotReads.EnabledRateLimitsAsync(db);
                if (rlRules.Count > 0)
                    rateLimiter.RecordTokens(rlRules, apiKey, provider, model,
                        promptTokens + completionTokens);
            }
            return;
        }

        var usageRow = new UsageRecord
        {
            Timestamp = now,
            Provider = provider,
            Model = model,
            ConnectionId = connectionId,
            ApiKey = apiKey,
            Endpoint = endpoint,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            Tokens = (promptTokens + completionTokens).ToString(),
            Status = status,
            Cost = cost,
            LatencyMs = latencyMs,
            Meta = ProxyJournal.MergeCorrelation(detail?.GetRawText() ?? error, corr),
            UserId = userId,
            UserKeyId = userKeyId,
        };
        db.UsageHistory.Add(usageRow);
        // SPEC-116: every logged request gets a proxy journal row (direct when no proxy).
        db.ProxyLogs.Add(ProxyJournal.Row(
            Guid.NewGuid().ToString("N"), now, provider, connectionId,
            status, error, latencyMs, endpoint, corr,
            await HotReads.OneProxyCurrentAsync(db, sdata), plog));
        // SPEC-116: settings.requestLogging=false pauses request-payload capture.
        var recording = !sdata.TryGetProperty("requestLogging", out var rl)
            || rl.ValueKind is not JsonValueKind.False;
        var ioOn = sdata.TryGetProperty("observability", out var obs)
            && obs.ValueKind is JsonValueKind.Object
            && obs.TryGetProperty("ioLogging", out var iol)
            && iol.ValueKind is JsonValueKind.True;
        if (recording)
        {
            db.RequestDetails.Add(new RequestDetail
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = now,
                Provider = provider,
                Model = model,
                ConnectionId = connectionId,
                Status = status,
                Data = detail?.GetRawText() ?? error ?? "{}",
                Prompt = ioOn ? ioPrompt : null,
                Response = ioOn ? ioResponse : null,
            });
        }
        await db.SaveChangesAsync();
        // SPEC-111: spend da vk + débito no ledger — caminho sync (sem writer).
        if (userId is not null || userKeyId is not null)
        {
            await UsageSideEffects.ApplyAsync(db, usageRow);
            await db.SaveChangesAsync();
        }
        await RollupDailyAsync(provider, model, promptTokens, completionTokens);

        // SPEC-039: credit tokens to matching rate-limit windows (tpm accounting)
        if (rateLimiter is not null)
        {
            var rlRules = await db.RateLimits.Where(r => r.Enabled).ToListAsync();
            if (rlRules.Count > 0)
                rateLimiter.RecordTokens(rlRules, apiKey, provider, model,
                    promptTokens + completionTokens);
        }

        // SPEC-041: credit tokens toward the key's daily quota counter
        await Routing.KeyQuota.CreditDailyAsync(db, apiKey, promptTokens + completionTokens, model);

        // SPEC-073: chat session tracking — upsert per (apiKey, model)
        var sess = await db.ChatSessions
            .FirstOrDefaultAsync(s => s.KeyId == apiKey && s.Model == model);
        if (sess is null)
        {
            db.ChatSessions.Add(new ChatSession
            {
                Id = Guid.NewGuid().ToString("N"),
                KeyId = apiKey, Model = model,
                StartedAt = now, LastSeenAt = now, MessageCount = 1,
            });
        }
        else { sess.LastSeenAt = now; sess.MessageCount++; }

        await db.SaveChangesAsync();
    }

    private Task RollupDailyAsync(string provider, string model, int prompt, int completion) =>
        RollupDailyOnAsync(db, provider, model, prompt, completion);

    /// <summary>SPEC-108: shared by the request path and the UsageWriter batch.</summary>
    public static async Task RollupDailyOnAsync(LlmRouterDbContext w, string provider, string model, int prompt, int completion)
    {
        var key = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var row = await w.UsageDaily.FirstOrDefaultAsync(r => r.DateKey == key);
        row ??= w.UsageDaily.Add(new UsageDaily { DateKey = key, Data = "{}" }).Entity;
        var data = JsonDocument.Parse(row.Data).RootElement;
        var dict = data.ValueKind == JsonValueKind.Object
            ? data.EnumerateObject().ToDictionary(kv => kv.Name, kv => kv.Value.Clone())
            : [];
        static JsonElement Bump(JsonElement? existing, string provider, string model, int p, int c)
        {
            var obj = existing is { ValueKind: JsonValueKind.Object } e
                ? e.EnumerateObject().ToDictionary(kv => kv.Name, kv => kv.Value.Clone())
                : new Dictionary<string, JsonElement>();
            var name = $"{provider}/{model}";
            if (obj.TryGetValue(name, out var cur) && cur.ValueKind == JsonValueKind.Object)
            {
                var o = cur.EnumerateObject().ToDictionary(kv => kv.Name, kv => kv.Value.Clone());
                o["tokens"] = JsonSerializer.SerializeToElement(
                    GetInt(cur, "tokens") + p + c);
                o["requests"] = JsonSerializer.SerializeToElement(GetInt(cur, "requests") + 1);
                obj[name] = JsonSerializer.SerializeToElement(o);
            }
            else
            {
                obj[name] = JsonSerializer.SerializeToElement(new Dictionary<string, int>
                {
                    ["tokens"] = p + c,
                    ["requests"] = 1,
                });
            }
            return JsonSerializer.SerializeToElement(obj);
        }
        static int GetInt(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        dict["models"] = Bump(dict.TryGetValue("models", out var m) ? m : null, provider, model, prompt, completion);
        dict["totals"] = JsonSerializer.SerializeToElement(new Dictionary<string, int>
        {
            ["tokens"] = (dict.TryGetValue("totals", out var t) && t.TryGetProperty("tokens", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt32() : 0) + prompt + completion,
            ["requests"] = (dict.TryGetValue("totals", out var t2) && t2.TryGetProperty("requests", out var tr) && tr.ValueKind == JsonValueKind.Number ? tr.GetInt32() : 0) + 1,
        });
        row.Data = JsonSerializer.Serialize(dict);
        await w.SaveChangesAsync();
    }
}
