using System.Text.Json;
using System.Text.RegularExpressions;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Routing;

/// <summary>
/// SPEC-084: faithful port of upstream lib/arenaEloSync.ts — fetches the
/// Arena leaderboard (api.wulong.dev) for the "text" and "code" categories,
/// normalizes ELO into task-fit scores in [0.4, 0.98], and stores them as
/// modelIntelligence rows (source arena_elo) that feed the `quality` and
/// `taskFit` factors of the intelligent scorer.
/// </summary>
public static class ArenaEloSync
{
    public const string ApiBase = "https://api.wulong.dev/arena-ai-leaderboards/v1/leaderboard";

    /// <summary>Upstream FETCH_CATEGORIES.</summary>
    public static readonly string[] FetchCategories = ["text", "code"];

    /// <summary>Upstream CATEGORY_TASK_MAP.</summary>
    public static readonly Dictionary<string, string[]> CategoryTaskMap = new()
    {
        ["text"] = ["default", "review", "documentation", "debugging"],
        ["code"] = ["coding"],
    };

    private static readonly string[] VendorPrefixes =
        ["anthropic/", "openai/", "google/", "meta/", "mistral/", "x-ai/", "deepseek/"];

    private static readonly Regex HarnessAnnotation =
        new(@"(?:-thinking|-max|-high|-medium|-low|-turbo-preview)$", RegexOptions.Compiled);

    /// <summary>Upstream normalizeModelName — strip vendor prefix + harness annotation.</summary>
    public static string NormalizeModelName(string raw)
    {
        var name = HarnessAnnotation.Replace(raw.ToLowerInvariant(), "");
        foreach (var p in VendorPrefixes)
            if (name.StartsWith(p)) { name = name[p.Length..]; break; }
        return name;
    }

    /// <summary>Upstream computeConfidence.</summary>
    public static string ComputeConfidence(int votes) =>
        votes >= 5000 ? "high" : votes >= 1000 ? "medium" : "low";

    /// <summary>Upstream ELO → task-fit normalization, clamped to [0.4, 0.98].</summary>
    public static double EloToTaskFit(double elo, double minElo, double maxElo) =>
        0.4 + 0.58 * ((elo - minElo) / Math.Max(maxElo - minElo, 1));

    /// <summary>Fetch one category's leaderboard; null on failure (fail-open).</summary>
    public static async Task<(string model, double score, int votes)[]?> FetchCategoryAsync(
        HttpClient http, string category, CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(
                await http.GetStringAsync($"{ApiBase}?leaderboard={category}", ct));
            var root = doc.RootElement;
            var modelsEl = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("models", out var m) ? m
                : root.ValueKind == JsonValueKind.Array ? root : default;
            if (modelsEl.ValueKind != JsonValueKind.Array) return null;
            var list = new List<(string, double, int)>();
            foreach (var e in modelsEl.EnumerateArray())
            {
                var name = e.TryGetProperty("model", out var mn) ? mn.GetString()
                    : e.TryGetProperty("name", out var nn) ? nn.GetString() : null;
                if (name is null) continue;
                var score = e.TryGetProperty("score", out var sc) ? sc.GetDouble()
                    : e.TryGetProperty("elo", out var elo) ? elo.GetDouble() : 0;
                var votes = e.TryGetProperty("votes", out var v) ? v.GetInt32() : 0;
                list.Add((name, score, votes));
            }
            return list.ToArray();
        }
        catch { return null; }
    }

    /// <summary>Run one sync cycle: fetch all categories, store arena_elo scores.</summary>
    public static async Task<(int models, List<string> errors)> SyncAsync(
        LlmRouterDbContext db, HttpClient http, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var written = 0;
        foreach (var category in FetchCategories)
        {
            var rows = await FetchCategoryAsync(http, category, ct);
            if (rows is null) { errors.Add($"fetch failed: {category}"); continue; }
            var min = rows.Min(r => r.score); var max = rows.Max(r => r.score);
            foreach (var (rawName, score, _) in rows)
            {
                var model = NormalizeModelName(rawName);
                var fit = Math.Round(EloToTaskFit(score, min, max), 4);
                foreach (var task in CategoryTaskMap[category])
                {
                    await ModelIntelligence.SetAsync(db, "arena_elo", model, task, fit);
                    written++;
                }
            }
        }
        await db.SaveChangesAsync(ct);
        return (written, errors);
    }
}
