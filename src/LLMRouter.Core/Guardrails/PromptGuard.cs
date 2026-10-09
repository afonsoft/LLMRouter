using System.Text.Json;
using System.Text.RegularExpressions;

namespace LLMRouter.Core.Guardrails;

/// <summary>
/// SPEC-023: prompt-injection guard — port of upstream
/// src/shared/utils/inputSanitizer.ts INJECTION_PATTERNS +
/// src/lib/guardrails/promptInjection.ts DEFAULT_GUARD_PATTERNS.
/// Severity scoring: low=1, medium=2, high=3; default block threshold "high"
/// (medium patterns are observe-only unless the threshold is lowered).
/// Modes: "block" | "warn" | "log" — warn/log flag but let the request pass.
/// </summary>
public static class PromptGuard
{
    public sealed record Detection(string Name, string Severity, string Match);

    /// <summary>Max chars scanned per request (upstream MAX_INJECTION_SCAN_BYTES).</summary>
    public const int MaxScanChars = 16 * 1024;

    private const string AuthorityFraming =
        @"(?:safe\s+(?:educational|research)\s+context|as\s+an?\s+(?:researcher|red[-\s]?teamer)" +
        @"|for\s+(?:testing|research|educational)\s+purposes\s+only|controlled\s+test\s+scenario)";
    private const string BypassRequest =
        @"(?:(?:ignore|bypass|disable|disregard|override|drop|turn\s+off)\s+(?:all\s+)?(?:of\s+)?" +
        @"(?:your|the|any)\s+(?:safety\s+|ethical\s+|content\s+)?" +
        @"(?:guidelines|restrictions|rules|filters|guardrails|safeguards|policies|limitations)" +
        @"|uncensored\s+(?:outputs?|responses?|answers?|replies|content|mode)" +
        @"|update\s+your\s+behaviou?r" +
        @"|(?:respond|answer|reply|proceed|continue|operate|act)\s+without\s+(?:any\s+)?" +
        @"(?:restrictions|limits|limitations|filters|censorship|guardrails))";
    private const int AuthorityWindow = 300;

    private static readonly (string Name, Regex Pattern, string Severity)[] Patterns =
    [
        ("system_override", new Regex(
            @"\b(ignore|disregard|forget)\s+(all\s+)?(previous|prior|above|earlier)\s+(instructions?|prompts?|rules?|context)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "high"),
        ("role_hijack", new Regex(
            @"\b(you\s+are\s+now|act\s+as\s+if|pretend\s+(to\s+be|you\s+are)|from\s+now\s+on\s+you\s+are)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "medium"),
        ("system_prompt_leak", new Regex(
            @"\b(reveals?|shows?|displays?|prints?|outputs?|repeats?)\s+((your|the)\s+)?(system|initial|hidden|original)\s+(prompt|instructions?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "high"),
        ("delimiter_injection", new Regex(
            @"(\[SYSTEM\]|\[INST\]|<<SYS>>|<\|im_start\|>|<\|system\|>|<\|user\|>)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "high"),
        ("jailbreak_dan", new Regex(
            @"\b(DAN|do\s+anything\s+now|jailbreak|developer\s+mode|enable\s+developer)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "medium"),
        ("encoding_evasion", new Regex(
            @"\b(base64\s+decode|rot13|hex\s+decode|unicode\s+escape)\b.*\b(instruction|prompt|command)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "medium"),
        ("authority_educational_framing", new Regex(
            $@"\b(?:{AuthorityFraming}\b[\s\S]{{0,{AuthorityWindow}}}?\b{BypassRequest}" +
            $@"|{BypassRequest}\b[\s\S]{{0,{AuthorityWindow}}}?\b{AuthorityFraming})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "medium"),
        // upstream DEFAULT_GUARD_PATTERNS
        ("system_override_inline", new Regex(@"\bsystem\s*:\s*override\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "high"),
        ("markdown_system_block", new Regex(@"```+\s*system\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "high"),
    ];

    private static readonly Dictionary<string, int> SeverityScore = new()
    { ["low"] = 1, ["medium"] = 2, ["high"] = 3 };

    /// <summary>All detections against the scan text.</summary>
    public static List<Detection> Detect(string scanText)
    {
        var hits = new List<Detection>();
        foreach (var (name, pattern, severity) in Patterns)
        {
            var m = pattern.Match(scanText);
            if (m.Success)
                hits.Add(new Detection(name, severity, m.Value[..Math.Min(50, m.Value.Length)]));
        }
        return hits;
    }

    /// <summary>Whether any detection meets the block threshold (default "high").</summary>
    public static bool ShouldBlock(IEnumerable<Detection> detections, string threshold = "high")
    {
        var min = SeverityScore.GetValueOrDefault(threshold, 3);
        return detections.Any(d => SeverityScore.GetValueOrDefault(d.Severity, 3) >= min);
    }

    /// <summary>
    /// Text to scan: walks messages[]/input[]/contents[]/instructions collecting
    /// string leaves under text|content|input|instructions keys, capped at
    /// MaxScanChars (mid-truncation like upstream — breaks patterns rather than
    /// blending them).
    /// </summary>
    public static string ScanText(JsonElement body)
    {
        var sb = new System.Text.StringBuilder(MaxScanChars);
        Extract(body, sb);
        if (sb.Length <= MaxScanChars) return sb.ToString();
        var half = MaxScanChars / 2;
        var s = sb.ToString();
        return s[..half] + "\n/* TRUNCATED */\n" + s[^half..];
    }

    private static void Extract(JsonElement el, System.Text.StringBuilder sb)
    {
        if (sb.Length > MaxScanChars * 2) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                sb.Append(el.GetString()).Append('\n');
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) Extract(item, sb);
                break;
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    // leaf fields carry prompt text; containers hold message arrays
                    if (p.Name is "text" or "content" or "input" or "instructions"
                        or "messages" or "contents" or "system" or "parts")
                        Extract(p.Value, sb);
                }
                break;
        }
    }
}
