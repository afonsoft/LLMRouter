using System.Text.Json;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Routing;

public sealed record RoutingPackOptions(
    int? HedgeDelayMs,
    IReadOnlyList<string> Predicates,
    string? MirrorModel)
{
    public static RoutingPackOptions From(Combo combo)
    {
        var hedgeDelay = ParseObject(combo.HedgeJson, "delayMs");
        var predicates = ParseStrings(combo.PredicatesJson);
        var mirror = ParseObjectString(combo.MirrorJson, "model");
        return new RoutingPackOptions(
            hedgeDelay is > 0 ? hedgeDelay : null,
            predicates,
            string.IsNullOrWhiteSpace(mirror) ? null : mirror);
    }

    private static int? ParseObject(string? json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            return doc.RootElement.TryGetProperty(property, out var value)
                && value.TryGetInt32(out var result) ? result : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? ParseObjectString(string? json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            return doc.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<string> ParseStrings(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "[]");
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .ToArray()
                : [];
        }
        catch (JsonException) { return []; }
    }
}
