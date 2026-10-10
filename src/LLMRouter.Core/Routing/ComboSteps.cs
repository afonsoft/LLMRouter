using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;

namespace LLMRouter.Core.Routing;

/// <summary>
/// SPEC-082 — combo steps 2.0 (upstream combos/steps.ts + invariants.ts +
/// autoPromote.ts + modelNameCollision.ts).
///
/// A combo's Models JSON array may contain:
///   - "provider/model"            → model step
///   - "provider/pat*rn*"          → provider-wildcard step
///   - "<comboName>"               → implicit combo-ref (no slash, name exists)
///   - {kind:"model", model, provider/providerId, weight, label,
///        prompt, tags, fallbackOnlyOnQuotaExhaustion}
///   - {kind:"combo-ref", comboName, weight, fallbackOnlyOnQuotaExhaustion}
///   - {kind:"provider-wildcard", providerId, modelPattern, weight}
/// Expansion produces a flat "provider/model" list (weights re-attached as
/// ~N); steps flagged fallbackOnlyOnQuotaExhaustion are appended after all
/// normal candidates (they only serve once regular steps fail on quota).
/// </summary>
public static class ComboSteps
{
    public const int MaxDepth = 4;
    private static readonly string[] NeverAutoPromoteKinds = ["random", "strict-random"];

    public sealed record Expanded(List<string> Models, List<string> QuotaOnly);

    /// <summary>Expand one combo's Models JSON into the flat candidate list.</summary>
    public static async Task<Expanded> ExpandAsync(
        LlmRouterDbContext db, ProviderRegistry registry, string modelsJson,
        HashSet<string>? visiting = null, int depth = 0)
    {
        var result = new Expanded([], []);
        visiting ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (depth > MaxDepth || string.IsNullOrWhiteSpace(modelsJson)) return result;

        List<JsonElement> steps;
        try
        {
            using var doc = JsonDocument.Parse(modelsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            steps = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch { return result; }

        var allNames = depth == 0
            ? (await db.Combos.AsNoTracking().Select(c => c.Name).ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var step in steps)
        {
            var (expanded, quotaOnly) = await ExpandStepAsync(
                db, registry, step, allNames, visiting, depth);
            result.Models.AddRange(expanded);
            result.QuotaOnly.AddRange(quotaOnly);
        }
        return result;
    }

    private static async Task<(List<string> models, List<string> quotaOnly)> ExpandStepAsync(
        LlmRouterDbContext db, ProviderRegistry registry, JsonElement step,
        HashSet<string>? allNames, HashSet<string> visiting, int depth)
    {
        var models = new List<string>();
        var quotaOnly = new List<string>();

        if (step.ValueKind == JsonValueKind.String)
        {
            var s = step.GetString()?.Trim() ?? "";
            if (s.Length == 0) return (models, quotaOnly);
            await ExpandTargetString(db, registry, s, suffix: null, models, quotaOnly,
                allNames, visiting, depth);
            return (models, quotaOnly);
        }
        if (step.ValueKind != JsonValueKind.Object) return (models, quotaOnly);

        var kind = step.TryGetProperty("kind", out var k) ? k.GetString() : null;
        var weight = step.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.Number
            ? w.GetInt32() : 0;
        var suffix = weight > 0 ? $"~{weight}" : null;
        var quotaFlag = step.TryGetProperty("fallbackOnlyOnQuotaExhaustion", out var fo)
            && fo.ValueKind == JsonValueKind.True;
        var sink = quotaFlag ? quotaOnly : models;

        if (kind == "combo-ref")
        {
            var name = Str(step, "comboName");
            if (name is null) return (models, quotaOnly);
            await ExpandComboRefAsync(db, registry, name, suffix, sink, visiting, depth);
        }
        else if (kind == "provider-wildcard")
        {
            var providerId = Str(step, "providerId") ?? Str(step, "provider");
            var pattern = Str(step, "modelPattern") ?? "*";
            if (providerId is not null)
                ExpandWildcard(registry, $"{providerId}/{pattern}", suffix, sink);
        }
        else
        {
            var model = Str(step, "model");
            if (model is not null)
            {
                var provider = Str(step, "providerId") ?? Str(step, "provider");
                var full = model.Contains('/') || provider is null ? model : $"{provider}/{model}";
                await ExpandTargetString(db, registry, full, suffix, sink, quotaOnly,
                    allNames, visiting, depth);
            }
        }
        return (models, quotaOnly);
    }

    private static async Task ExpandTargetString(
        LlmRouterDbContext db, ProviderRegistry registry, string target, string? suffix,
        List<string> sink, List<string> quotaOnly,
        HashSet<string>? allNames, HashSet<string> visiting, int depth)
    {
        var bare = target.Split('~')[0];
        if (bare.Contains('/'))
        {
            var after = bare[(bare.IndexOf('/') + 1)..];
            if (after.Contains('*')) { ExpandWildcard(registry, bare, suffix ?? SuffixOf(target), sink); return; }
            sink.Add(suffix is null ? target : bare + suffix);
            return;
        }
        // implicit combo-ref: bare name matching an existing combo
        if (allNames is null || allNames.Contains(bare))
            await ExpandComboRefAsync(db, registry, bare, suffix ?? SuffixOf(target), sink, visiting, depth);
        else
            sink.Add(suffix is null ? target : bare + suffix);
    }

    private static async Task ExpandComboRefAsync(
        LlmRouterDbContext db, ProviderRegistry registry, string comboName, string? suffix,
        List<string> sink, HashSet<string> visiting, int depth)
    {
        if (depth >= MaxDepth || !visiting.Add(comboName)) return;
        var sub = await db.Combos.AsNoTracking().FirstOrDefaultAsync(c => c.Name == comboName);
        if (sub is not null)
        {
            var ex = await ExpandAsync(db, registry, sub.Models, visiting, depth + 1);
            sink.AddRange(ex.Models.Select(m => suffix is null ? m : StripSuffix(m) + suffix));
            sink.AddRange(ex.QuotaOnly.Select(m => suffix is null ? m : StripSuffix(m) + suffix));
        }
        visiting.Remove(comboName);
    }

    private static void ExpandWildcard(ProviderRegistry registry, string pattern, string? suffix, List<string> sink)
    {
        var slash = pattern.IndexOf('/');
        if (slash <= 0) return;
        var providerId = pattern[..slash];
        var modelPattern = pattern[(slash + 1)..];
        var provider = registry.GetProvider(providerId);
        if (provider?.Models is null) return;
        var regex = Glob(modelPattern);
        foreach (var m in provider.Models.Where(m => regex.IsMatch(m.Id)))
            sink.Add($"{provider.Id}/{m.Id}{suffix}");
    }

    private static Regex Glob(string pattern) => new(
        "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? Str(JsonElement o, string prop) =>
        o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim() : null;

    private static string? SuffixOf(string target) =>
        target.Contains('~') ? target[target.IndexOf('~')..] : null;

    private static string StripSuffix(string target) => target.Split('~')[0];

    /// <summary>
    /// Upstream invariants.ts — combo.allowedProviders / allowedModelFamilies /
    /// invariant.* restrict which steps are valid. Throws ComboInvariantError.
    /// </summary>
    public sealed class ComboInvariantError : Exception
    {
        public ComboInvariantError(string message) : base(message) { }
    }

    private static readonly (string family, Regex pattern)[] FamilyPatterns =
    [
        ("gpt", new Regex(@"^gpt(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("claude", new Regex(@"^claude(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("gemini", new Regex(@"^gemini(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("glm", new Regex(@"^glm(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("kimi", new Regex(@"^kimi(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("deepseek", new Regex(@"^deepseek(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("qwen", new Regex(@"^(?:qwen|qwq)(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("llama", new Regex(@"^llama(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("minimax", new Regex(@"^minimax(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("mistral", new Regex(@"^(?:mistral|mixtral)(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    ];

    public static string? ModelFamily(string model)
    {
        var bare = model[(model.LastIndexOf('/') + 1)..];
        foreach (var (family, pattern) in FamilyPatterns)
            if (pattern.IsMatch(bare)) return family;
        return null;
    }

    /// <summary>
    /// combo.AllowedProviders / AllowedFamilies (JSON arrays, set at create)
    /// restrict steps — mirrors validateComboInvariant.
    /// </summary>
    public static void ValidateInvariant(string comboName, JsonElement[] steps,
        IReadOnlyCollection<string> allowedProviders, IReadOnlyCollection<string> allowedFamilies)
    {
        if (allowedProviders.Count == 0 && allowedFamilies.Count == 0) return;
        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            string? model = null, provider = null;
            if (step.ValueKind == JsonValueKind.String)
            {
                model = step.GetString();
                if (model is null) continue;
                // combo-ref steps skip validation (upstream: kind combo-ref → return)
                provider = model.Contains('/') ? model[..model.IndexOf('/')] : "";
            }
            else if (step.ValueKind == JsonValueKind.Object)
            {
                if (Str(step, "kind") == "combo-ref") continue;
                model = Str(step, "model");
                provider = Str(step, "providerId") ?? Str(step, "provider")
                    ?? (model?.Contains('/') == true ? model[..model.IndexOf('/')] : "");
            }
            if (model is null) continue;
            var family = ModelFamily(model);
            var violates =
                (allowedProviders.Count > 0 && provider is not null && !allowedProviders.Contains(provider))
                || (allowedFamilies.Count > 0 && (family is null || !allowedFamilies.Contains(family)));
            if (violates)
            {
                var name = model.Contains('/') ? model : $"{provider}/{model}";
                throw new ComboInvariantError(
                    $"Combo \"{comboName}\" target {i + 1} ({name}) violates its invariant");
            }
        }
    }

    /// <summary>
    /// Upstream autoPromote.ts — when settings.data.comboAutoPromoteEnabled and
    /// a combo step succeeded, promote it to the front of combo.Models.
    /// Only reorders model-kind entries (combo-ref/wildcard keep position).
    /// Skips random/strict-random strategies (order is meaningless there).
    /// </summary>
    public static async Task AutoPromoteAsync(LlmRouterDbContext db, JsonElement sdata,
        string? comboName, string winningProviderModel)
    {
        if (comboName is null) return;
        var enabled = sdata.ValueKind == JsonValueKind.Object
            && sdata.TryGetProperty("comboAutoPromoteEnabled", out var ap)
            && ap.ValueKind == JsonValueKind.True;
        if (!enabled) return;

        var combo = await db.Combos.FirstOrDefaultAsync(c => c.Name == comboName);
        if (combo is null || combo.Kind is null
            || NeverAutoPromoteKinds.Contains(combo.Kind, StringComparer.OrdinalIgnoreCase))
            return;
        List<JsonElement> steps;
        try
        {
            using var doc = JsonDocument.Parse(combo.Models);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            steps = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch { return; }
        if (steps.Count < 2) return;

        var bare = winningProviderModel.Split('~')[0];
        var idx = steps.FindIndex(s =>
        {
            var v = s.ValueKind == JsonValueKind.String ? s.GetString() : Str(s, "model");
            return v is not null && string.Equals(v.Split('~')[0], bare, StringComparison.OrdinalIgnoreCase);
        });
        if (idx <= 0) return;

        var winner = steps[idx];
        steps.RemoveAt(idx);
        // promote above the leading model steps but keep any leading combo-ref
        // anchors pinned (upstream keeps non-model head steps in place)
        var head = 0;
        while (head < steps.Count
            && steps[head].ValueKind == JsonValueKind.Object
            && Str(steps[head], "kind") is "combo-ref" or "provider-wildcard")
            head++;
        steps.Insert(head, winner);
        combo.Models = JsonSerializer.Serialize(steps);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// SPEC-082 composite tiers (upstream compositeTiers.ts) — validate the
    /// `tiers` array shape; each tier needs a steps array of model strings.
    /// </summary>
    public static List<string> ValidateCompositeTiers(JsonElement tiers)
    {
        var errors = new List<string>();
        if (tiers.ValueKind != JsonValueKind.Array)
            return ["tiers must be an array"];
        var i = 0;
        foreach (var tier in tiers.EnumerateArray())
        {
            i++;
            if (tier.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"tiers[{i}] must be an object");
                continue;
            }
            if (!tier.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"tiers[{i}].steps must be an array");
                continue;
            }
            var j = 0;
            foreach (var s in steps.EnumerateArray())
            {
                j++;
                if (s.ValueKind == JsonValueKind.String) continue;
                if (s.ValueKind == JsonValueKind.Object && Str(s, "model") is not null) continue;
                errors.Add($"tiers[{i}].steps[{j}] is not a model step");
            }
        }
        return errors;
    }
}
