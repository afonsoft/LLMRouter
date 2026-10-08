using System.Reflection;
using System.Text.Json;

namespace LLMRouter.Core.Registry;

/// <summary>
/// Loads the provider registry embedded as JSON resources — a verbatim data port of
/// open-sse/config/providers/index.ts (288 providers) and
/// src/shared/constants/providers/* (370 UI catalog entries) from OmniRoute.
/// </summary>
public class ProviderRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, ProviderEntry> _providers;
    private readonly Dictionary<string, string> _aliasToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (UiProviderEntry Entry, string Category)> _uiProviders = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ProviderEntry> Providers => _providers;

    public ProviderRegistry()
    {
        var asm = typeof(ProviderRegistry).Assembly;
        var list = Load<List<ProviderEntry>>(asm, "providers.json") ?? [];
        _providers = list.Where(p => !string.IsNullOrEmpty(p.Id))
            .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var p in _providers.Values.Distinct())
        {
            _aliasToId[p.Id] = p.Id;
            if (!string.IsNullOrEmpty(p.Alias)) _aliasToId[p.Alias] = p.Id;
            foreach (var a in p.Aliases ?? []) _aliasToId[a] = p.Id;
        }

        var ui = Load<Dictionary<string, Dictionary<string, UiProviderEntry>>>(asm, "ui-providers.json")
            ?? new Dictionary<string, Dictionary<string, UiProviderEntry>>();
        foreach (var (category, entries) in ui)
        {
            foreach (var (key, entry) in entries)
            {
                if (string.IsNullOrEmpty(entry.Id)) entry.Id = key;
                _uiProviders[key] = (entry, category);
            }
        }
    }

    private static T? Load<T>(Assembly asm, string name)
    {
        var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(name, StringComparison.Ordinal));
        if (res is null) return default;
        using var s = asm.GetManifestResourceStream(res)!;
        return JsonSerializer.Deserialize<T>(s, JsonOpts);
    }

    public ProviderEntry? GetProvider(string idOrAlias)
        => _providers.TryGetValue(ResolveProviderId(idOrAlias), out var p) ? p : null;

    public string ResolveProviderId(string aliasOrId)
        => _aliasToId.TryGetValue(aliasOrId, out var id) ? id : aliasOrId;

    public IEnumerable<(UiProviderEntry Entry, string Category)> UiProviders()
        => _uiProviders.Values.OrderBy(v => v.Entry.Name ?? v.Entry.Id, StringComparer.OrdinalIgnoreCase);

    public (UiProviderEntry? Entry, string? Category) GetUiProvider(string id)
        => _uiProviders.TryGetValue(id, out var v) ? (v.Entry, v.Category) : (null, null);

    /// <summary>Provider ids that need an API key credential.</summary>
    public static bool RequiresApiKey(ProviderEntry p) => p.AuthType is "apikey" or "optional";

    public string? GetModelsUrl(ProviderEntry p) => p.ModelsUrl ?? p.TestKeyModelsUrl;
}
