using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMRouter.Core.Extras;

/// <summary>
/// SPEC-035: scans locally-installed LLM CLI tools' config/credentials files for
/// API keys and OAuth tokens that can be imported as provider connections.
/// Upstream parity: claude, codex, cursor, kiro, trae, zed, agy, command-code, opencode.
/// </summary>
public static class CliCredentialScanner
{
    public sealed record Finding(string Tool, string Provider, string CredentialType, string Value, string Path, string Label)
    {
        public string Masked => Value.Length <= 8 ? "****" : $"{Value[..4]}…{Value[^4..]}";
        public string Id => $"{Tool}:{Path}:{CredentialType}";
    }

    /// <summary>Candidate files per tool → provider hint. Rel paths are under HOME.</summary>
    private static readonly (string Tool, string Provider, string RelPath)[] Candidates =
    [
        ("claude", "anthropic", ".claude/.credentials.json"),
        ("claude", "anthropic", ".claude.json"),
        ("codex", "openai", ".codex/auth.json"),
        ("cursor", "openai", ".cursor/auth.json"),
        ("kiro", "aws-bedrock", ".kiro/settings.json"),
        ("trae", "openai", ".trae/auth.json"),
        ("zed", "anthropic", ".zed/settings.json"),
        ("agy", "gemini", ".gemini/antigravity/auth.json"),
        ("agy", "gemini", ".gemini/oauth_creds.json"),
        ("command-code", "anthropic", ".command-code/auth.json"),
        ("opencode", "multi", ".local/share/opencode/auth.json"),
        ("gemini", "gemini", ".gemini/oauth_creds.json"),
        ("qwen", "openai", ".qwen/auth.json"),
        ("grok", "xai", ".grok/auth.json"),
    ];

    /// <summary>JSON keys whose string values count as usable credentials.</summary>
    private static readonly Regex CredKey = new(
        "^(api_?key|key|token|access_?token|id_?token|refresh_?token|apikey)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Secretish = new(
        "^(sk-[A-Za-z0-9_-]{8,}|sk-ant-[A-Za-z0-9_-]{8,}|key-[A-Za-z0-9_-]{8,}|[A-Za-z0-9_-]{24,}|ya29\\.[A-Za-z0-9_-]+|eyJ[A-Za-z0-9_-]+\\.)",
        RegexOptions.Compiled);

    public static List<Finding> Scan(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var findings = new List<Finding>();
        foreach (var (tool, provider, rel) in Candidates)
        {
            var path = Path.Combine(home, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            JsonNode? root;
            try { root = JsonNode.Parse(File.ReadAllText(path)); }
            catch { continue; }
            foreach (var (keyPath, value, credType) in Extract(root))
            {
                var p = provider == "multi" ? GuessProvider(keyPath, value) : provider;
                findings.Add(new Finding(tool, p, credType, value, path,
                    $"{tool} → {keyPath}"));
            }
        }
        // dedup identical values (same token stored in several files)
        return findings.GroupBy(f => f.Value).Select(g => g.First()).ToList();
    }

    /// <summary>Walk the JSON tree collecting credential-looking strings.</summary>
    private static IEnumerable<(string KeyPath, string Value, string CredType)> Extract(JsonNode? node, string path = "")
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj)
                {
                    var child = path == "" ? kv.Key : $"{path}.{kv.Key}";
                    foreach (var hit in Extract(kv.Value, child)) yield return hit;
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    foreach (var hit in Extract(arr[i], $"{path}[{i}]")) yield return hit;
                break;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Length >= 16:
                var leaf = path.Split('.', '[')[^1];
                if (CredKey.IsMatch(leaf) || Secretish.IsMatch(s))
                    yield return (path, s, CredType(leaf));
                break;
        }
    }

    private static string CredType(string leaf) =>
        leaf.Contains("refresh", StringComparison.OrdinalIgnoreCase) ? "refresh_token"
        : leaf.Contains("id_token", StringComparison.OrdinalIgnoreCase) ? "id_token"
        : leaf.Contains("token", StringComparison.OrdinalIgnoreCase) ? "access_token"
        : "api_key";

    private static string GuessProvider(string keyPath, string value)
    {
        var s = (keyPath + " " + value[..Math.Min(16, value.Length)]).ToLowerInvariant();
        if (s.Contains("openai") || s.StartsWith("sk-")) return "openai";
        if (s.Contains("anthropic") || s.Contains("claude") || s.Contains("sk-ant")) return "anthropic";
        if (s.Contains("gemini") || s.Contains("google") || s.Contains("ya29")) return "gemini";
        if (s.Contains("groq")) return "groq";
        if (s.Contains("xai") || s.Contains("grok")) return "xai";
        if (s.Contains("deepseek")) return "deepseek";
        if (s.Contains("qwen") || s.Contains("dashscope")) return "qwen";
        if (s.Contains("mistral")) return "mistral";
        // opencode auth.json stores { "<providerId>": { "key": ... } } — first path segment IS the provider
        var root = keyPath.Split('.')[0];
        if (root is not ("" or "key" or "token" or "tokens")) return root;
        return "unknown";
    }
}
