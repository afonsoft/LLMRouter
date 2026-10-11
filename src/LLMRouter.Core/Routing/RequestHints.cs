using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Registry;

namespace LLMRouter.Core.Routing;

/// <summary>SPEC-102: request-level routing hints — OpenRouter-style `provider` object.</summary>
public sealed record RouteHints(
    string[]? Order,
    bool AllowFallbacks,
    bool RequireParameters,
    double? MaxPromptPrice,
    double? MaxCompletionPrice,
    string? Sort,
    string? DataCollection);

/// <summary>Thrown when `provider.order` names a slug the registry can't resolve → 400.</summary>
public sealed class RouteHintException(string slug) : Exception($"Unknown provider slug '{slug}'")
{
    public string Slug { get; } = slug;
}

public static class RequestHints
{
    /// <summary>Parse the optional `provider` object from a request body; null when absent.</summary>
    public static RouteHints? Parse(JsonElement? body)
    {
        if (body is not { } b || !b.TryGetProperty("provider", out var p) || p.ValueKind != JsonValueKind.Object)
            return null;
        string[]? order = null;
        if (p.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Array)
            order = o.EnumerateArray().Select(e => e.GetString() ?? "")
                .Where(x => x.Length > 0).ToArray();
        var allowFallbacks = !(p.TryGetProperty("allow_fallbacks", out var af) && af.ValueKind == JsonValueKind.False);
        var requireParams = p.TryGetProperty("require_parameters", out var rp) && rp.ValueKind == JsonValueKind.True;
        double? maxPrompt = null, maxCompletion = null;
        if (p.TryGetProperty("max_price", out var mp) && mp.ValueKind == JsonValueKind.Object)
        {
            if (mp.TryGetProperty("prompt", out var pp) && pp.ValueKind == JsonValueKind.Number) maxPrompt = pp.GetDouble();
            if (mp.TryGetProperty("completion", out var cp) && cp.ValueKind == JsonValueKind.Number) maxCompletion = cp.GetDouble();
        }
        var sort = p.TryGetProperty("sort", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var dc = p.TryGetProperty("data_collection", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        if (order is null && allowFallbacks && !requireParams && maxPrompt is null && maxCompletion is null && sort is null && dc is null)
            return null;
        return new RouteHints(order, allowFallbacks, requireParams, maxPrompt, maxCompletion, sort, dc);
    }

    /// <summary>
    /// Model-list level hints: `order` restricts+sequences providers, `require_parameters`
    /// filters to fully-capable models, `max_price` drops over-cap models, `sort` reorders
    /// by price / measured latency / measured throughput.
    /// </summary>
    public static async Task<List<string>> ApplyToModelsAsync(
        List<string> models, RouteHints h, LlmRouterDbContext db,
        ModelResolver resolver, ProviderRegistry registry, ComboPlanner planner,
        JsonElement sdata, JsonElement? requestBody, Dictionary<string, string> aliases)
    {
        // order: restrict to listed provider slugs, in the listed sequence.
        if (h.Order is { Length: > 0 } ord)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < ord.Length; i++)
            {
                if (index.ContainsKey(ord[i])) continue;
                if (registry.GetProvider(ord[i]) is null
                    && await NodeResolver.ResolveAsync(db, ord[i]) is null)
                    throw new RouteHintException(ord[i]);
                index[ord[i]] = i;
            }
            models = models.Where(m => index.ContainsKey(resolver.Resolve(m, aliases).Provider))
                .OrderBy(m => index[resolver.Resolve(m, aliases).Provider])
                .ToList();
        }

        // require_parameters: keep only models that support every required capability
        // in the request (vision/tools/json_schema), reusing combo capability detection.
        if (h.RequireParameters && requestBody is { } body)
        {
            var required = ComboPlanner.DetectRequiredCapabilities(body);
            if (required.Count > 0)
            {
                var overrides = await HotReads.CapabilityOverridesAsync(db);
                models = models.Where(m =>
                {
                    var caps = planner.GetCapabilitiesForModel(m, overrides);
                    return required.All(c => caps.Contains(c));
                }).ToList();
            }
        }

        // max_price: drop models above the $/Mtok caps (unknown price stays — can't be evaluated).
        if (h.MaxPromptPrice is not null || h.MaxCompletionPrice is not null)
            models = models.Where(m =>
            {
                var (p, mid) = resolver.Resolve(m, aliases);
                var price = Usage.PricingService.GetPrice(sdata, registry, p, mid);
                if (price is null) return true;
                if (h.MaxPromptPrice is { } mp && price.Value.input > mp) return false;
                if (h.MaxCompletionPrice is { } mc && price.Value.output > mc) return false;
                return true;
            }).ToList();

        // sort: reorder by registry price / measured p50 latency / measured tokens-per-sec.
        if (h.Sort is { } sort)
        {
            switch (sort.ToLowerInvariant())
            {
                case "price":
                    models = models.OrderBy(m =>
                    {
                        var (p, mid) = resolver.Resolve(m, aliases);
                        return Usage.PricingService.GetPrice(sdata, registry, p, mid)?.input
                            ?? double.MaxValue;
                    }).ToList();
                    break;
                case "latency":
                    var lat = new Dictionary<string, double>();
                    foreach (var m in models)
                    {
                        var (p, mid) = resolver.Resolve(m, aliases);
                        lat[m] = await P50LatencyAsync(db, p, mid);
                    }
                    models = models.OrderBy(m => lat[m]).ToList();
                    break;
                case "throughput":
                    var tps = new Dictionary<string, double>();
                    foreach (var m in models)
                    {
                        var (p, mid) = resolver.Resolve(m, aliases);
                        tps[m] = await TokensPerSecAsync(db, p, mid);
                    }
                    models = models.OrderByDescending(m => tps[m]).ToList();
                    break;
            }
        }
        return models;
    }

    /// <summary>Target-list level hints: `data_collection` filter + `allow_fallbacks` truncation.</summary>
    public static List<ResolvedTarget> ApplyToTargets(List<ResolvedTarget> targets, RouteHints h)
    {
        if (string.Equals(h.DataCollection, "deny", StringComparison.OrdinalIgnoreCase))
            targets = targets.Where(t =>
                t.Provider.TrainsOnData != true && !ConnTrainsOnData(t.Connection.Data)).ToList();
        if (!h.AllowFallbacks)
            targets = targets.Take(1).ToList();
        return targets;
    }

    /// <summary>Measured p50 upstream latency for a provider/model from usageHistory (MaxValue when unknown).</summary>
    private static async Task<double> P50LatencyAsync(LlmRouterDbContext db, string provider, string model)
    {
        var lats = await db.UsageHistory
            .Where(u => u.Provider == provider && u.Model == model && u.Status == "ok" && u.LatencyMs > 0)
            .OrderByDescending(u => u.Timestamp).Take(50)
            .Select(u => u.LatencyMs).ToListAsync();
        if (lats.Count == 0) return double.MaxValue;
        lats.Sort();
        return lats[lats.Count / 2];
    }

    /// <summary>Measured tokens/sec (completion tokens per latency second) for a provider/model.</summary>
    private static async Task<double> TokensPerSecAsync(LlmRouterDbContext db, string provider, string model)
    {
        var rows = await db.UsageHistory
            .Where(u => u.Provider == provider && u.Model == model && u.Status == "ok" && u.LatencyMs > 0)
            .OrderByDescending(u => u.Timestamp).Take(50)
            .Select(u => new { u.CompletionTokens, u.LatencyMs }).ToListAsync();
        var tok = rows.Sum(r => (long)r.CompletionTokens);
        var ms = rows.Sum(r => r.LatencyMs);
        return ms <= 0 ? 0 : tok * 1000.0 / ms;
    }

    private static bool ConnTrainsOnData(string? data)
    {
        if (string.IsNullOrEmpty(data)) return false;
        try
        {
            var el = JsonDocument.Parse(data).RootElement;
            if (el.TryGetProperty("trainsOnData", out var t) && t.ValueKind == JsonValueKind.True) return true;
        }
        catch { /* best-effort: failure is non-fatal */ }
        return false;
    }
}
