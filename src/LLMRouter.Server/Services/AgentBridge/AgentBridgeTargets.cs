using System.Text.RegularExpressions;

namespace LLMRouter.Server.Services.AgentBridge;

/// <summary>A MITM target agent — port of src/mitm/types.ts MitmTarget.</summary>
public sealed record MitmTarget(
    string Id,
    string Name,
    string[] Hosts,
    string[] DetectedModels,
    string? DetectCommand,
    string Viability = "supported");

/// <summary>
/// Registry of AgentBridge MITM targets — ported verbatim from
/// src/mitm/targets/*.ts (hosts, detected models, detection command).
/// </summary>
public static partial class AgentBridgeTargets
{
    private const string ClaudeSonnet45 = "claude-sonnet-4.5";
    private const string Claude35Sonnet = "claude-3.5-sonnet";
    private const string Gpt4o = "gpt-4o";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public static readonly MitmTarget[] All =
    [
        new("antigravity", "Antigravity IDE",
            ["daily-cloudcode-pa.googleapis.com", "cloudcode-pa.googleapis.com",
             "daily-cloudcode-pa.sandbox.googleapis.com", "autopush-cloudcode-pa.sandbox.googleapis.com"],
            ["gemini-2.5-pro", "gemini-2.5-flash", ClaudeSonnet45],
            "which antigravity"),
        new("kiro", "Kiro IDE",
            ["q.amazonaws.com", "codewhisperer.us-east-1.amazonaws.com"],
            [ClaudeSonnet45, "claude-sonnet-4"],
            "which kiro"),
        new("copilot", "GitHub Copilot",
            ["api.githubcopilot.com", "copilot-proxy.githubusercontent.com"],
            [Gpt4o, Claude35Sonnet, "gemini-2.0-flash"],
            null),
        new("ghe-copilot", "GitHub Enterprise Copilot",
            [],
            [Gpt4o, Claude35Sonnet, "gemini-2.0-flash"],
            null),
        new("codex", "OpenAI Codex",
            ["chatgpt.com"],
            ["gpt-4.1", "gpt-4o-mini"],
            "which codex"),
        new("cursor", "Cursor IDE",
            ["api2.cursor.sh"],
            [ClaudeSonnet45, Gpt4o],
            "which cursor"),
        new("zed", "Zed",
            ["api.zed.dev"],
            [Claude35Sonnet, Gpt4o],
            "which zed"),
        new("claude-code", "Claude Code",
            ["api.anthropic.com"],
            [ClaudeSonnet45, "claude-opus-4.5"],
            "which claude"),
        new("open-code", "OpenCode",
            ["opencode.ai"],
            [Gpt4o, Claude35Sonnet],
            "which opencode"),
        new("trae", "Trae",
            ["trae.invalid"],
            [ClaudeSonnet45, Gpt4o],
            "which trae"),
    ];

    /// <summary>Default bypass list — ported verbatim from passthrough.ts.</summary>
    private static readonly Regex[] DefaultBypass =
    [
        new Regex(@"\.bank\.", RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout),
        new Regex(@"(^|\.)gov(\.|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout),
        new Regex(@"(^|\.)okta\.com$", RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout),
        new Regex(@"(^|\.)auth0\.com$", RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout),
    ];

    /// <summary>Glob match (* wildcard) — linear scan, ReDoS-safe (ported).</summary>
    public static bool GlobMatch(string hostname, string pattern)
    {
        var segments = pattern.ToLowerInvariant().Split('*');
        if (segments.Length > 9) return false; // upstream bound: >8 segments rejected
        var h = hostname.ToLowerInvariant();
        var pos = 0;
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (seg.Length == 0) continue;
            var idx = h.IndexOf(seg, pos, StringComparison.Ordinal);
            if (idx < 0) return false;
            if (i == 0 && idx != 0) return false;           // first segment must anchor
            pos = idx + seg.Length;
        }
        // last segment must anchor at the end when pattern doesn't end with '*'
        if (segments[^1].Length > 0 && !h.EndsWith(segments[^1], StringComparison.Ordinal))
            return false;
        return true;
    }

    public static MitmTarget? Resolve(string hostname)
    {
        var h = hostname.ToLowerInvariant();
        return All.FirstOrDefault(t => t.Hosts.Any(x => x.Equals(h, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool ShouldBypass(string hostname, IReadOnlyList<string> userBypass)
    {
        if (DefaultBypass.Any(re => re.IsMatch(hostname))) return true;
        return userBypass.Any(p => GlobMatch(hostname, p));
    }

    /// <summary>bypass > target > passthrough (upstream plan 11 §4.6).</summary>
    public static (string kind, MitmTarget? target) RouteConnection(string hostname, IReadOnlyList<string> userBypass)
    {
        if (ShouldBypass(hostname, userBypass)) return ("bypass", null);
        var t = Resolve(hostname);
        return t is not null ? ("target", t) : ("passthrough", null);
    }
}
