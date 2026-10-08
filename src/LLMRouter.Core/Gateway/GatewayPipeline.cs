using System.Diagnostics;
using System.Text.Json;
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
    ModelResolver resolver)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Resolve a model name into the ordered list of targets to try
    /// (single target for provider/model, cascade for combos).
    /// </summary>
    public async Task<List<ResolvedTarget>> ResolveAsync(
        string model,
        JsonElement? requestBody = null,
        CancellationToken ct = default)
    {
        var settings = await db.Settings.FirstOrDefaultAsync(ct);
        var aliases = LoadAliases(settings?.Data ?? "{}");

        // model-combo mappings: upstream-facing model name → combo name (auto-wrap)
        var mapping = await db.Kv
            .Where(k => k.Scope == "modelComboMappings" && k.Key == model)
            .Select(k => k.Value).FirstOrDefaultAsync(ct);
        var effectiveModel = mapping ?? model;

        var combo = await db.Combos.FirstOrDefaultAsync(c => c.Name == effectiveModel, ct);
        List<string> models;
        if (combo is not null)
        {
            var list = JsonSerializer.Deserialize<List<string>>(combo.Models) ?? [];
            var rr = string.Equals(combo.Kind, "round-robin", StringComparison.OrdinalIgnoreCase);
            models = ComboPlanner.GetRotatedModels(list, combo.Name,
                rr ? "round-robin" : "fallback", combo.StickyLimit);
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
                models = planner.ReorderByCapabilities(models, required);
        }

        var targets = new List<ResolvedTarget>();
        foreach (var m in models)
        {
            var (providerId, upstreamModel) = resolver.Resolve(m, aliases);
            var provider = registry.GetProvider(providerId)
                ?? await NodeResolver.ResolveAsync(db, providerId, ct);
            if (provider is null) continue;
            var conns = await db.ProviderConnections
                .Where(c => c.Provider == provider.Id && c.IsActive)
                .OrderBy(c => c.Priority).ThenBy(c => c.Name)
                .ToListAsync(ct);
            foreach (var c in conns.Where(c => !Resilience.CooldownTracker.IsCooling(c.Id)))
                targets.Add(new ResolvedTarget(provider, c, upstreamModel, combo?.Name));
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
                if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
        }
        catch { }
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
        catch { }
        return map;
    }

    /// <summary>Record a completed (or failed) request.</summary>
    public async Task LogUsageAsync(
        string provider, string model, string? connectionId, string apiKey,
        string endpoint, int promptTokens, int completionTokens,
        string status, string? error, long latencyMs, JsonElement? detail = null)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        var cost = Usage.PricingService.ComputeCost(
            await Usage.PricingService.SettingsDataAsync(db), registry,
            provider, model, promptTokens, completionTokens) ?? 0;
        db.UsageHistory.Add(new UsageRecord
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
            Meta = detail?.GetRawText() ?? error,
        });
        db.RequestDetails.Add(new RequestDetail
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = now,
            Provider = provider,
            Model = model,
            ConnectionId = connectionId,
            Status = status,
            Data = detail?.GetRawText(),
        });
        await db.SaveChangesAsync();
        await RollupDailyAsync(provider, model, promptTokens, completionTokens);
    }

    private async Task RollupDailyAsync(string provider, string model, int prompt, int completion)
    {
        var key = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var row = await db.UsageDaily.FirstOrDefaultAsync(r => r.DateKey == key);
        row ??= db.UsageDaily.Add(new UsageDaily { DateKey = key, Data = "{}" }).Entity;
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
        await db.SaveChangesAsync();
    }
}
