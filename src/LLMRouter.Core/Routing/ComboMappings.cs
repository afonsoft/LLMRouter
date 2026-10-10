using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Routing;

/// <summary>
/// SPEC-083: model-combo-mappings — glob patterns + priority + enabled flag
/// (upstream model-combo-mappings.js). Stored in kv scope
/// "modelComboMappings": key = model pattern, value = plain combo name
/// (legacy) or JSON {"combo":..,"priority":n,"enabled":bool}.
/// Resolution: enabled rows sorted by priority desc, then pattern length
/// desc (most specific wins); first glob match returns the combo name.
/// </summary>
public static class ComboMappings
{
    public const string Scope = "modelComboMappings";

    public sealed record Entry(string Pattern, string Combo, int Priority, bool Enabled);

    public static List<Entry> Parse(IEnumerable<KvEntry> rows)
    {
        var list = new List<Entry>();
        foreach (var r in rows)
        {
            var v = r.Value?.Trim() ?? "";
            if (v.StartsWith('{'))
            {
                try
                {
                    using var d = JsonDocument.Parse(v);
                    var e = d.RootElement;
                    list.Add(new Entry(r.Key,
                        e.TryGetProperty("combo", out var c) ? c.GetString() ?? "" : "",
                        e.TryGetProperty("priority", out var p) ? p.GetInt32() : 0,
                        !e.TryGetProperty("enabled", out var en) || en.GetBoolean()));
                    continue;
                }
                catch { /* malformed → treat as plain string */ }
            }
            list.Add(new Entry(r.Key, v, 0, true));
        }
        return list;
    }

    /// <summary>Glob match: '*' (any run) and '?' (one char), case-insensitive.</summary>
    public static bool GlobMatch(string pattern, string value)
    {
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, rx,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>Resolve the winning combo for a model id, or null.</summary>
    public static string? Match(IEnumerable<Entry> entries, string model) =>
        entries.Where(e => e.Enabled && GlobMatch(e.Pattern, model))
            .OrderByDescending(e => e.Priority)
            .ThenByDescending(e => e.Pattern.Length)
            .Select(e => e.Combo)
            .FirstOrDefault(c => c.Length > 0);

    public static async Task<string?> MatchAsync(LlmRouterDbContext db, string model)
    {
        var rows = await db.Kv.AsNoTracking().Where(k => k.Scope == Scope).ToListAsync();
        return Match(Parse(rows), model);
    }

    /// <summary>Serialize an entry for kv storage (plain string when defaults).</summary>
    public static string Encode(string combo, int priority, bool enabled) =>
        priority == 0 && enabled
            ? combo
            : JsonSerializer.Serialize(new { combo, priority, enabled });
}
