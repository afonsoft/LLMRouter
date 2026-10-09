using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMRouter.Core.Extras;

/// <summary>
/// SPEC-033: RTK-style named compression filters (upstream /dashboard/context/rtk
/// — enabledFilters, skipRules, preservePatterns, compressRoles, maxLineLength).
/// Applied to message text content when tokenSaver is enabled. Config comes from
/// settings.tokenSaver: {enabled, filters:[names], maxLineLength, compressRoles,
/// skipRules, preservePatterns}.
/// Catalog: collapseWhitespace, dedupLines, dropEmptyLines, stripComments,
/// stripAnsi, truncateLines, redactSecrets, minifyJson.
/// </summary>
public static partial class RtkFilters
{
    public sealed record Config(
        bool Enabled, string[] Filters, int MaxLineLength,
        string[] CompressRoles, string[] SkipRules, string[] PreservePatterns)
    {
        public static Config Default => new(false,
            ["collapseWhitespace", "dedupLines", "dropEmptyLines"], 500,
            ["system", "user", "assistant"], [], []);
    }

    public static readonly (string Name, string Desc)[] Catalog =
    [
        ("collapseWhitespace", "Collapse runs of spaces/tabs"),
        ("dedupLines", "Drop consecutive identical lines"),
        ("dropEmptyLines", "Remove empty lines"),
        ("stripComments", "Remove // # -- <!-- --> and /* */ comments"),
        ("stripAnsi", "Remove ANSI escape sequences"),
        ("truncateLines", "Cap each line at maxLineLength"),
        ("redactSecrets", "Replace API-key/token patterns with [REDACTED]"),
        ("minifyJson", "Compact whole-content JSON payloads"),
    ];

    public static Config Parse(JsonElement? settings)
    {
        var d = Config.Default;
        if (settings is not { ValueKind: JsonValueKind.Object } s
            || !s.TryGetProperty("tokenSaver", out var t)) return d;
        return new(
            t.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True,
            StrArr(t, "filters") ?? d.Filters,
            t.TryGetProperty("maxLineLength", out var m) && m.TryGetInt32(out var mi) ? mi : 500,
            StrArr(t, "compressRoles") ?? d.CompressRoles,
            StrArr(t, "skipRules") ?? [],
            StrArr(t, "preservePatterns") ?? []);
    }

    private static string[]? StrArr(JsonElement o, string key) =>
        o.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToArray()
            : null;

    /// <summary>Apply the configured filters to one text blob, in catalog order.</summary>
    public static string Apply(string text, Config cfg)
    {
        var preserve = cfg.PreservePatterns
            .Select(p => { try { return new Regex(p); } catch { return null; } })
            .Where(r => r is not null).Cast<Regex>().ToArray();
        var skip = cfg.SkipRules
            .Select(p => { try { return new Regex(p); } catch { return null; } })
            .Where(r => r is not null).Cast<Regex>().ToArray();
        var f = new HashSet<string>(cfg.Filters);
        var inBlock = false;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var outLines = new List<string>(lines.Length);
        string? prev = null;
        foreach (var raw in lines)
        {
            var line = raw;
            if (preserve.Length > 0 && preserve.Any(r => r.IsMatch(line)))
            { outLines.Add(line); prev = line; continue; }
            if (skip.Length > 0 && skip.Any(r => r.IsMatch(line))) continue;
            if (f.Contains("stripAnsi")) line = Ansi().Replace(line, "");
            if (f.Contains("stripComments"))
            {
                var t = line.TrimStart();
                if (inBlock)
                {
                    var end = t.IndexOf("*/", StringComparison.Ordinal);
                    if (end < 0) continue;
                    line = t[(end + 2)..]; inBlock = false;
                }
                t = line.TrimStart();
                var bs = t.IndexOf("/*", StringComparison.Ordinal);
                if (bs >= 0)
                {
                    var be = t.IndexOf("*/", bs + 2, StringComparison.Ordinal);
                    if (be < 0) { line = t[..bs]; inBlock = true; }
                    else line = t[..bs] + t[(be + 2)..];
                    t = line.TrimStart();
                }
                if (t.StartsWith("//") || t.StartsWith('#') || t.StartsWith("--")
                    || t.StartsWith("<!--")) continue;
            }
            if (f.Contains("collapseWhitespace")) line = SpaceRun().Replace(line.Trim(), " ");
            if (f.Contains("truncateLines") && line.Length > cfg.MaxLineLength)
                line = line[..cfg.MaxLineLength] + "…";
            if (f.Contains("redactSecrets")) line = Secrets().Replace(line, "[REDACTED]");
            if (f.Contains("dropEmptyLines") && line.Trim().Length == 0) { prev = null; continue; }
            if (f.Contains("dedupLines") && line == prev) continue;
            outLines.Add(line);
            prev = line;
        }
        var joined = string.Join('\n', outLines).Trim();
        if (f.Contains("minifyJson") && joined.StartsWith('{'))
            try { joined = JsonNode.Parse(joined)!.ToJsonString(); } catch { }
        return joined;
    }

    /// <summary>Apply filters across an OpenAI/Claude-style body: content/text/system/prompt strings
    /// inside messages whose role is in compressRoles (or non-message strings).</summary>
    public static (JsonElement Body, int SavedChars) ApplyToBody(JsonElement body, Config cfg)
    {
        var node = JsonNode.Parse(body.GetRawText())!;
        var saved = 0;
        CompressNode(node, cfg, inMessages: false, ref saved);
        return (JsonSerializer.SerializeToElement(node), saved);
    }

    private static void CompressNode(JsonNode node, Config cfg, bool inMessages, ref int saved)
    {
        if (node is JsonObject o)
        {
            var role = o["role"]?.GetValue<string>();
            var isMsg = inMessages && role is not null;
            var compress = !isMsg || cfg.CompressRoles.Contains(role!, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in o.ToList())
            {
                if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s)
                    && (kv.Key is "content" or "text" or "prompt" or "system" || s.Contains('\n'))
                    && compress)
                {
                    var c = Apply(s, cfg);
                    saved += s.Length - c.Length;
                    if (c != s) o[kv.Key] = c;
                }
                else if (kv.Value is JsonObject oo)
                    CompressNode(oo, cfg, inMessages || kv.Key == "messages", ref saved);
                else if (kv.Value is JsonArray aa)
                    CompressNode(aa, cfg, inMessages || kv.Key == "messages", ref saved);
            }
        }
        else if (node is JsonArray a)
            foreach (var item in a) CompressNode(item, cfg, inMessages, ref saved);
    }

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpaceRun();
    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]|\x1B\][^\x07]*\x07")]
    private static partial Regex Ansi();
    [GeneratedRegex(@"(sk-[A-Za-z0-9_\-]{8,}|AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9\-]+|Bearer\s+\S{10,}|eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{5,})")]
    private static partial Regex Secrets();
}
