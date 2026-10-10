using System.Text.RegularExpressions;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Routing;

/// <summary>
/// SPEC-072: virtual auto/* combos — resolved at request time from the live
/// candidate pool (active connections × provider registry models), no row
/// persisted unless the user materializes one via combos/duplicate.
/// auto/{known} uses category heuristics; auto/{anything-else} is a substring
/// filter over the pool (e.g. auto/glm → every model containing "glm").
/// </summary>
public static partial class AutoCombos
{
    public sealed record VirtualCombo(string Id, string Description);

    public static readonly VirtualCombo[] All =
    [
        new("auto/best", "Flagship-tier models across connected providers"),
        new("auto/coding", "Code-focused models"),
        new("auto/fast", "Lightweight/mini/nano models"),
        new("auto/free", "Free-tier models"),
    ];

    public static bool IsAuto(string model) =>
        model.StartsWith("auto/", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("auto-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every "provider/model" reachable via an active connection.</summary>
    public static async Task<List<string>> CandidatePoolAsync(
        LlmRouterDbContext db, ProviderRegistry registry, CancellationToken ct = default)
    {
        var conns = await db.ProviderConnections.Where(c => c.IsActive)
            .OrderBy(c => c.Priority).ThenBy(c => c.Name).ToListAsync(ct);
        var pool = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in conns)
        {
            var p = registry.GetProvider(c.Provider);
            if (p?.Models is not null)
                foreach (var m in p.Models)
                    if (seen.Add($"{p.Id}/{m.Id}")) pool.Add($"{p.Id}/{m.Id}");
            // live-synced catalog (SPEC-071) — custom providers/nodes carry models
            // the static registry doesn't know about
            var synced = await db.SyncedModels
                .Where(sm => sm.Provider == c.Provider && sm.Available).ToListAsync(ct);
            foreach (var sm in synced)
                if (seen.Add($"{c.Provider}/{sm.Model}")) pool.Add($"{c.Provider}/{sm.Model}");
        }
        return pool;
    }

    /// <summary>Resolve an auto/* name into an ordered candidate list.</summary>
    public static async Task<List<string>> ResolveCandidatesAsync(
        LlmRouterDbContext db, ProviderRegistry registry, string name, CancellationToken ct = default)
    {
        var pool = await CandidatePoolAsync(db, registry, ct);
        var cut = name.IndexOf('/');
        var suffix = (cut > 0 ? name[(cut + 1)..] : name[4..]).ToLowerInvariant();
        return suffix switch
        {
            "best" => pool.Where(k => FlagshipRe().IsMatch(k)).ToList(),
            "coding" => pool.Where(k => CodingRe().IsMatch(k)).ToList(),
            "fast" => pool.Where(k => FastRe().IsMatch(k)).ToList(),
            "free" => pool.Where(k => k.Contains("free", StringComparison.OrdinalIgnoreCase)).ToList(),
            _ => pool.Where(k => k.Contains(suffix, StringComparison.OrdinalIgnoreCase)).ToList(),
        };
    }

    [GeneratedRegex("(gpt-4|gpt-5|o[0-9]|claude-(3-5|3[.]5|sonnet|opus)|gemini-(1[.]5|2|3)|llama-3|deepseek-v|mistral-large|command-r|qwen2[.]5-72|grok)", RegexOptions.IgnoreCase)]
    private static partial Regex FlagshipRe();

    [GeneratedRegex("(codex|code|coder|codestral|starcoder|deepseek|devstral)", RegexOptions.IgnoreCase)]
    private static partial Regex CodingRe();

    [GeneratedRegex("(mini|nano|flash|haiku|lite|small|instant|turbo|1[.]5-flash|4o-mini|3[.]5-turbo)", RegexOptions.IgnoreCase)]
    private static partial Regex FastRe();
}
