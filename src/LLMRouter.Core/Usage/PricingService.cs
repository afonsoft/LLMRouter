using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;

namespace LLMRouter.Core.Usage;

/// <summary>
/// Cost computation: user price overrides in settings.data.catalogUserPricing
/// ({ "provider/model": { "input": x, "output": y } } per 1M tokens) take
/// precedence over the registry model's own pricing entry.
/// </summary>
public static class PricingService
{
    public const string OverridesKey = "catalogUserPricing";

    /// <summary>Returns (inputPer1M, outputPer1M) USD, or null when unknown.</summary>
    public static (double input, double output)? GetPrice(
        JsonElement settingsData, ProviderRegistry registry, string provider, string model)
    {
        if (settingsData.ValueKind == JsonValueKind.Object
            && settingsData.TryGetProperty(OverridesKey, out var ov)
            && ov.ValueKind == JsonValueKind.Object
            && ov.TryGetProperty($"{provider}/{model}", out var e))
        {
            var p = ParsePrice(e);
            if (p is not null) return p;
        }
        var prov = registry.GetProvider(provider);
        var m = prov?.Models?.FirstOrDefault(x =>
            string.Equals(x.Id, model, StringComparison.OrdinalIgnoreCase));
        if (m?.Pricing is JsonElement je) return ParsePrice(je);
        if (m?.Pricing is not null)
        {
            try { return ParsePrice(JsonSerializer.SerializeToElement(m.Pricing)); }
            catch { return null; }
        }
        return null;
    }

    public static double? ComputeCost(
        JsonElement settingsData, ProviderRegistry registry,
        string provider, string model, long promptTokens, long completionTokens)
    {
        var p = GetPrice(settingsData, registry, provider, model);
        return p is null ? null : (promptTokens * p.Value.input + completionTokens * p.Value.output) / 1_000_000.0;
    }

    public static (double input, double output)? ParsePrice(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        double Get(params string[] names)
        {
            foreach (var n in names)
                if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number)
                    return v.GetDouble();
            return -1;
        }
        var i = Get("input", "prompt", "inputPer1M", "input_price");
        var o = Get("output", "completion", "outputPer1M", "output_price");
        return i < 0 && o < 0 ? null : (Math.Max(i, 0), Math.Max(o, 0));
    }

    public static async Task<JsonElement> SettingsDataAsync(LlmRouterDbContext db)
    {
        var row = await db.Settings.FindAsync(1);
        if (row is null) return default;
        try { return JsonDocument.Parse(row.Data).RootElement.Clone(); }
        catch { return default; }
    }
}
