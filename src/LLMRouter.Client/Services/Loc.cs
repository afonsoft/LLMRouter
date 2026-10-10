using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;

namespace LLMRouter.Client.Services;

/// <summary>
/// Localization — loads /locales/{lang}.json (upstream flat files),
/// nested key lookup "sidebar.home". Languages: en, pt-BR, es.
/// </summary>
public sealed class Loc(HttpClient http, IJSRuntime js)
{
    private Dictionary<string, JsonElement> _en = [];
    private Dictionary<string, JsonElement> _cur = [];

    public string Lang { get; private set; } = "en";
    public event Action? Changed;
    public static readonly string[] Languages = ["en", "pt-BR", "es"];

    public async Task LoadAsync()
    {
        var saved = await js.InvokeAsync<string?>("llmrouter.get", "language")
            ?? await js.InvokeAsync<string?>("llmrouter.get", "i18nextLng") ?? "en";
        _en = await Fetch("en");
        await SetAsync(saved);
    }

    public async Task SetAsync(string lang)
    {
        if (!Languages.Contains(lang)) lang = "en";
        Lang = lang;
        _cur = lang == "en" ? _en : await Fetch(lang);
        await js.InvokeVoidAsync("llmrouter.set", "language", lang);
        Changed?.Invoke();
    }

    private async Task<Dictionary<string, JsonElement>> Fetch(string lang)
    {
        try
        {
            var doc = await http.GetFromJsonAsync<JsonElement>($"locales/{lang}.json");
            if (doc.ValueKind == JsonValueKind.Object)
                return doc.EnumerateObject().ToDictionary(kv => kv.Name, kv => kv.Value.Clone());
        }
        catch { /* best-effort: failure is non-fatal */ }
        return [];
    }

    /// <summary>Translate a dotted key with fallback to English then the key itself.</summary>
    public string T(string key, string? fallback = null)
    {
        if (Lookup(_cur, key) is { } s) return s;
        if (Lookup(_en, key) is { } e) return e;
        return fallback ?? key;
    }

    private static string? Lookup(Dictionary<string, JsonElement> dict, string key)
    {
        // keys may be "sidebar.home" nested or flat — try nested walk then flat
        var parts = key.Split('.');
        JsonElement? cur = null;
        var node = dict;
        var ok = true;
        foreach (var p in parts)
        {
            if (node.TryGetValue(p, out var v)) { cur = v; node = ToDict(v); }
            else { ok = false; break; }
        }
        if (ok && cur is { ValueKind: JsonValueKind.String } s) return s.GetString();
        // flat key with dots?
        if (dict.TryGetValue(key, out var f) && f.ValueKind == JsonValueKind.String)
            return f.GetString();
        // last segment of a common namespace (e.g. "sidebar.home" → dict["sidebar"]["home"])
        return null;
    }

    private static Dictionary<string, JsonElement> ToDict(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object
            ? el.EnumerateObject().ToDictionary(kv => kv.Name, kv => kv.Value)
            : [];
}
