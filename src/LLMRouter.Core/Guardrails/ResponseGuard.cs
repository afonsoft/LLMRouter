using System.Text.Json;
using System.Text.RegularExpressions;

namespace LLMRouter.Core.Guardrails;

/// <summary>
/// SPEC-107: response-side guardrails — scans assembled upstream responses
/// (and per-chunk SSE payloads) for credential shapes and PII, reusing the
/// PromptGuard severity model (low/medium/high + block threshold).
/// Modes: block | redact | warn | log.
/// </summary>
public static class ResponseGuard
{
    public sealed record Cfg(bool Enabled, string Mode, string Threshold, string[] ExtraPatterns);
    public sealed record Detection(string Name, string Severity, int Count);

    /// <summary>settings.guardrails.response = {enabled, mode, threshold, patterns[]}.</summary>
    public static Cfg Read(JsonElement sdata)
    {
        var enabled = false; var mode = "log"; var threshold = "high";
        var extra = new List<string>();
        if (sdata.ValueKind == JsonValueKind.Object
            && sdata.TryGetProperty("guardrails", out var g) && g.ValueKind == JsonValueKind.Object
            && g.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            if (r.TryGetProperty("enabled", out var e)) enabled = e.ValueKind == JsonValueKind.True;
            if (r.TryGetProperty("mode", out var m) && m.GetString() is { Length: > 0 } mm) mode = mm;
            if (r.TryGetProperty("threshold", out var t) && t.GetString() is { Length: > 0 } tt) threshold = tt;
            if (r.TryGetProperty("patterns", out var p) && p.ValueKind == JsonValueKind.Array)
                foreach (var x in p.EnumerateArray())
                    if (x.GetString() is { Length: > 0 } xs) extra.Add(xs);
        }
        return new Cfg(enabled, mode, threshold, extra.ToArray());
    }

    private static readonly (string Name, Regex Pattern, string Severity)[] Patterns =
    [
        ("api_key", new Regex(
            @"\b(?:sk|pk|api|key|token|secret)[-_][A-Za-z0-9_\-]{16,}\b|\bsk-[A-Za-z0-9_\-]{16,}\b|\bAKIA[0-9A-Z]{16}\b|\b(?:ghp|gho|ghu|ghs|ghr|xox[baprs])-[A-Za-z0-9_\-]{16,}\b",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)), "high"),
        ("private_key", new Regex(
            @"-----BEGIN [A-Z ]*PRIVATE KEY-----",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)), "high"),
        ("jwt", new Regex(
            @"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{5,}\b",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)), "medium"),
        ("email", new Regex(
            @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)), "low"),
        ("phone", new Regex(
            @"\+?\d{1,3}[\s.\-]?\(?\d{2,4}\)?[\s.\-]?\d{3,4}[\s.\-]?\d{4}\b",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)), "low"),
        // credit_card handled separately — regex candidate + Luhn validation
        ("credit_card", new Regex(
            @"\b(?:\d[ \-]?){13,19}\b",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)), "medium"),
    ];

    private static readonly Dictionary<string, int> SeverityScore = new()
    { ["low"] = 1, ["medium"] = 2, ["high"] = 3 };

    /// <summary>Detections in text. The credit_card pattern requires at least
    /// one Luhn-valid candidate; user-supplied extra patterns are "high".</summary>
    public static List<Detection> Detect(string text, Cfg? cfg = null)
    {
        var hits = new List<Detection>();
        foreach (var (name, pattern, severity) in Patterns)
        {
            var matches = pattern.Matches(text);
            var n = name == "credit_card"
                ? matches.Count(m => Luhn(m.Value))
                : matches.Count;
            if (n > 0) hits.Add(new Detection(name, severity, n));
        }
        if (cfg?.ExtraPatterns is { Length: > 0 } extras)
            foreach (var xp in extras)
                try
                {
                    var n = Regex.Matches(text, xp, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500)).Count;
                    if (n > 0) hits.Add(new Detection($"custom:{xp[..Math.Min(24, xp.Length)]}", "high", n));
                }
                catch { }
        return hits;
    }

    public static bool ShouldBlock(IEnumerable<Detection> detections, string threshold = "high")
    {
        var min = SeverityScore.GetValueOrDefault(threshold, 3);
        return detections.Any(d => SeverityScore.GetValueOrDefault(d.Severity, 3) >= min);
    }

    /// <summary>Replace every match with [REDACTED:name]. Patterns match
    /// printable runs (no quote/escape chars) so raw-JSON replacement is safe.</summary>
    public static string Redact(string text, Cfg? cfg = null)
    {
        foreach (var (name, pattern, _) in Patterns)
            text = name == "credit_card"
                ? pattern.Replace(text, m => Luhn(m.Value) ? $"[REDACTED:{name}]" : m.Value)
                : pattern.Replace(text, $"[REDACTED:{name}]");
        if (cfg?.ExtraPatterns is { Length: > 0 } extras)
            foreach (var xp in extras)
                try
                {
                    text = Regex.Replace(text, xp, "[REDACTED:custom]",
                        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
                }
                catch { }
        return text;
    }

    /// <summary>Regex-escape a string for use in patterns (e.g. settings-supplied).</summary>
    public static string Escape(string s) => Regex.Escape(s);

    /// <summary>Luhn check on the digits of a candidate card number.</summary>
    public static bool Luhn(string candidate)
    {
        var digits = candidate.Where(char.IsDigit).ToArray();
        if (digits.Length is < 13 or > 19) return false;
        var sum = 0; var dbl = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d; dbl = !dbl;
        }
        return sum % 10 == 0;
    }
}
