using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMRouter.Core.Extras;

/// <summary>
/// Memory search scoring (SPEC-026): term-frequency + recency rank over kv
/// `memory.items` entries ({id, at, content, tags}). Embeddings stay a
/// provider dependency — when an embeddings-capable model is configured the
/// caller may rerank; this lexical score is the fallback/default.
/// </summary>
public static class MemorySearch
{
    private static readonly Regex Terms = new(@"\w+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));

    /// <summary>Score one item: sum(term frequencies) × recency boost
    /// (items decay with a 30-day half-life). 0 = no match.</summary>
    public static double Score(string query, JsonElement item, DateTime? now = null)
    {
        var content = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var tags = item.TryGetProperty("tags", out var t) ? t.GetString() ?? "" : "";
        var hay = content + " " + tags;
        var qterms = Terms.Matches(query.ToLowerInvariant()).Select(m => m.Value).Distinct().ToArray();
        if (qterms.Length == 0) return 0;
        double tf = 0;
        var lower = hay.ToLowerInvariant();
        var matched = 0;
        foreach (var qt in qterms)
        {
            var count = 0; var idx = 0;
            while ((idx = lower.IndexOf(qt, idx, StringComparison.Ordinal)) >= 0) { count++; idx += qt.Length; }
            if (count > 0) { matched++; tf += count; }
        }
        if (matched == 0) return 0;
        var coverage = (double)matched / qterms.Length;
        var recency = 1.0;
        if (item.TryGetProperty("at", out var at) && DateTime.TryParse(at.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var when))
        {
            var days = ((now ?? DateTime.UtcNow) - when).TotalDays;
            recency = Math.Pow(0.5, Math.Max(0, days) / 30.0);
        }
        return tf * coverage * (0.5 + 0.5 * recency);
    }

    /// <summary>Ranked search over the items array; returns clones with a
    /// `score` property, descending.</summary>
    public static List<JsonElement> Search(JsonElement items, string? q, int limit = 50)
    {
        var all = items.EnumerateArray().Select(x => x.Clone());
        if (string.IsNullOrWhiteSpace(q)) return all.Take(limit).ToList();
        return all
            .Select(x => (x, s: Score(q, x)))
            .Where(t => t.s > 0)
            .OrderByDescending(t => t.s)
            .Take(limit)
            .Select(t =>
            {
                var o = JsonNode.Parse(t.x.GetRawText())!.AsObject();
                o["score"] = Math.Round(t.s, 4);
                return JsonDocument.Parse(o.ToJsonString()).RootElement.Clone();
            }).ToList();
    }
}
