namespace LLMRouter.Shared.Ux;

public static class UrlFilters
{
    public static IReadOnlyDictionary<string, string> Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new Dictionary<string, string>();
        return query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0)
            .GroupBy(parts => Uri.UnescapeDataString(parts[0]), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.Last()[1]), StringComparer.OrdinalIgnoreCase);
    }

    public static string Build(IEnumerable<KeyValuePair<string, string?>> values)
    {
        return string.Join("&", values
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));
    }
}
