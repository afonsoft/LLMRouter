using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Gateway;

/// <summary>
/// SPEC-101: parses settings.data.observability —
/// { metricsAuth: bool, otlp: { endpoint, protocol, headers } }.
/// metricsAuth gates /metrics behind a valid api key; otlp enables the OTLP
/// exporter for metrics+traces. Both off/absent by default.
/// </summary>
public static class ObservabilitySettings
{
    public sealed record OtlpConfig(string Endpoint, string Protocol,
        IReadOnlyDictionary<string, string> Headers);

    public sealed record Config(bool MetricsAuth, OtlpConfig? Otlp);

    public static readonly Config Default = new(false, null);

    /// <summary>Parse the observability node from a settings.data JSON payload.</summary>
    public static Config Parse(JsonElement sdata)
    {
        if (sdata.ValueKind != JsonValueKind.Object
            || !sdata.TryGetProperty("observability", out var ob)
            || ob.ValueKind != JsonValueKind.Object)
            return Default;
        var metricsAuth = ob.TryGetProperty("metricsAuth", out var ma) && ma.ValueKind == JsonValueKind.True;
        return new Config(metricsAuth, ParseOtlp(ob));
    }

    private static OtlpConfig? ParseOtlp(JsonElement ob)
    {
        if (!ob.TryGetProperty("otlp", out var ot) || ot.ValueKind != JsonValueKind.Object
            || !ot.TryGetProperty("endpoint", out var ep) || ep.ValueKind != JsonValueKind.String
            || ep.GetString() is not { Length: > 0 } endpoint)
            return null;
        var protocol = ot.TryGetProperty("protocol", out var pr) && pr.ValueKind == JsonValueKind.String
            ? pr.GetString()! : "grpc";
        return new OtlpConfig(endpoint, protocol, ParseHeaders(ot));
    }

    private static Dictionary<string, string> ParseHeaders(JsonElement ot)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ot.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
            foreach (var kv in h.EnumerateObject())
                if (kv.Value.ValueKind == JsonValueKind.String)
                    headers[kv.Name] = kv.Value.GetString()!;
        return headers;
    }

    /// <summary>
    /// Best-effort early read of settings.data.observability before the host is
    /// built (needed to decide OTLP exporter registration). Returns Default on
    /// first boot (no settings table yet) or any read error.
    /// </summary>
    public static Config LoadEarly(string dbPath)
    {
        try
        {
            using var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
                .UseSqlite($"Data Source={dbPath}").Options);
            var row = db.Settings.AsNoTracking().FirstOrDefault();
            return row is null ? Default : Parse(JsonDocument.Parse(row.Data).RootElement);
        }
        catch { return Default; }
    }
}
