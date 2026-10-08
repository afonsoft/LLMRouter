using System.Text.RegularExpressions;
using LLMRouter.Core.Registry;

namespace LLMRouter.Core.Routing;

/// <summary>
/// Ported from open-sse/services/model.js: parse "provider/model" or bare model names,
/// resolve aliases, and infer a provider from the model prefix when nothing else matches.
/// </summary>
public partial class ModelResolver
{
    private readonly ProviderRegistry _registry;

    // Config-driven prefix → provider inference (first match wins, fallback "openai").
    private static readonly (Regex Re, string Provider)[] PrefixProviders =
    [
        (CodexAutoReviewRe(), "codex"),
        (Gpt56DotRe(), "codex"),
        (Gpt6DashRe(), "codex"),
        (GptDaybreakRe(), "codex"),
        (GptReserveRe(), "codex"),
        (ClaudeRe(), "anthropic"),
        (BedrockRe(), "bedrock"),
        (GeminiRe(), "gemini"),
        (GptRe(), "openai"),
        (O1Re(), "openai"),
        (DeepseekRe(), "openrouter"),
    ];

    private static readonly Dictionary<string, string> BuiltinModelAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["grok-build"] = "gcli/grok-build",
    };

    public ModelResolver(ProviderRegistry registry) => _registry = registry;

    public record ModelRef(string? Provider, string Model, bool IsAlias, string? ProviderAlias);

    /// <summary>Parse model string: "provider/model", "alias/model", or bare model/alias.</summary>
    public ModelRef Parse(string? modelStr)
    {
        if (string.IsNullOrEmpty(modelStr))
            return new ModelRef(null, "", false, null);

        var slash = modelStr.IndexOf('/');
        if (slash >= 0)
        {
            var providerOrAlias = modelStr[..slash];
            return new ModelRef(_registry.ResolveProviderId(providerOrAlias), modelStr[(slash + 1)..], false, providerOrAlias);
        }
        return new ModelRef(null, modelStr, true, null);
    }

    /// <summary>
    /// Resolve a bare model to a provider+model via the caller's alias map, builtin
    /// aliases, then the model-name prefix inference. Returns (provider, model).
    /// </summary>
    public (string Provider, string Model) Resolve(string modelStr, IReadOnlyDictionary<string, string>? aliases)
    {
        var parsed = Parse(modelStr);
        if (!parsed.IsAlias && parsed.Provider is not null)
            return (parsed.Provider, parsed.Model);

        var model = parsed.Model;
        if (aliases is not null && aliases.TryGetValue(model, out var target) && !string.IsNullOrEmpty(target))
        {
            var t = Parse(target);
            if (t.Provider is not null) return (t.Provider, t.Model);
        }
        if (BuiltinModelAliases.TryGetValue(model, out var builtin))
        {
            var t = Parse(builtin);
            if (t.Provider is not null) return (t.Provider, t.Model);
        }
        return (InferProviderFromModelName(model), model);
    }

    /// <summary>Provider inference from model name prefix (fallback "openai").</summary>
    public static string InferProviderFromModelName(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return "openai";
        var m = modelName.ToLowerInvariant();
        foreach (var (re, provider) in PrefixProviders)
            if (re.IsMatch(m)) return provider;
        return "openai";
    }

    [GeneratedRegex(@"^codex-auto-review$", RegexOptions.IgnoreCase)] private static partial Regex CodexAutoReviewRe();
    [GeneratedRegex(@"^gpt-[56]\.", RegexOptions.IgnoreCase)] private static partial Regex Gpt56DotRe();
    [GeneratedRegex(@"^gpt-6-", RegexOptions.IgnoreCase)] private static partial Regex Gpt6DashRe();
    [GeneratedRegex(@"^gpt-daybreak-", RegexOptions.IgnoreCase)] private static partial Regex GptDaybreakRe();
    [GeneratedRegex(@"^gpt-reserve", RegexOptions.IgnoreCase)] private static partial Regex GptReserveRe();
    [GeneratedRegex(@"^claude-", RegexOptions.IgnoreCase)] private static partial Regex ClaudeRe();
    [GeneratedRegex(@"^(us|eu|ap|global)\.(anthropic|meta|amazon|mistral|xai)\.", RegexOptions.IgnoreCase)] private static partial Regex BedrockRe();
    [GeneratedRegex(@"^gemini-", RegexOptions.IgnoreCase)] private static partial Regex GeminiRe();
    [GeneratedRegex(@"^gpt-", RegexOptions.IgnoreCase)] private static partial Regex GptRe();
    [GeneratedRegex(@"^o[134]", RegexOptions.IgnoreCase)] private static partial Regex O1Re();
    [GeneratedRegex(@"^deepseek-", RegexOptions.IgnoreCase)] private static partial Regex DeepseekRe();
}
