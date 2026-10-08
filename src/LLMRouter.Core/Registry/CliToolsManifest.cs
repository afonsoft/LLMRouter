using System.Diagnostics;
using System.Text.Json;

namespace LLMRouter.Core.Registry;

/// <summary>
/// CLI coding tools manifest (SPEC-009, mirrors upstream config/cli-tools-manifest.json
/// + cliTools/): each tool declares a detection binary, the env/config vars that point
/// it at this router, and an install hint. Values use {baseUrl}/{apiKey} placeholders.
/// </summary>
public static class CliToolsManifest
{
    public sealed record CliTool(
        string Id, string Name, string Binary, string InstallHint,
        string Inbound, Dictionary<string, string> Env);

    public static readonly CliTool[] Tools =
    [
        new("claude-code", "Claude Code", "claude", "npm i -g @anthropic-ai/claude-code",
            "claude", new() { ["ANTHROPIC_BASE_URL"] = "{baseUrl}", ["ANTHROPIC_AUTH_TOKEN"] = "{apiKey}" }),
        new("codex", "Codex CLI", "codex", "npm i -g @openai/codex",
            "openai", new() { ["OPENAI_BASE_URL"] = "{baseUrl}/v1", ["OPENAI_API_KEY"] = "{apiKey}" }),
        new("gemini-cli", "Gemini CLI", "gemini", "npm i -g @google/gemini-cli",
            "gemini", new() { ["GEMINI_BASE_URL"] = "{baseUrl}/v1beta", ["GEMINI_API_KEY"] = "{apiKey}" }),
        new("cursor", "Cursor", "cursor-agent", "cursor.com → Agent config",
            "openai", new() { ["OPENAI_BASE_URL"] = "{baseUrl}/v1", ["OPENAI_API_KEY"] = "{apiKey}" }),
        new("opencode", "OpenCode", "opencode", "npm i -g opencode-ai",
            "openai", new() { ["OPENAI_BASE_URL"] = "{baseUrl}/v1", ["OPENAI_API_KEY"] = "{apiKey}" }),
        new("windsurf", "Windsurf", "windsurf", "windsurf.com → settings.json",
            "openai", new() { ["OPENAI_BASE_URL"] = "{baseUrl}/v1", ["OPENAI_API_KEY"] = "{apiKey}" }),
        new("kiro", "Kiro CLI", "kiro-cli", "kiro.dev → CLI install",
            "claude", new() { ["ANTHROPIC_BASE_URL"] = "{baseUrl}", ["ANTHROPIC_AUTH_TOKEN"] = "{apiKey}" }),
        new("zed", "Zed", "zed", "zed.dev → settings.json anthropic/openai sections",
            "openai", new() { ["OPENAI_BASE_URL"] = "{baseUrl}/v1", ["OPENAI_API_KEY"] = "{apiKey}" }),
        new("aider", "Aider", "aider", "pipx install aider-chat",
            "openai", new() { ["OPENAI_API_BASE"] = "{baseUrl}/v1", ["OPENAI_API_KEY"] = "{apiKey}" }),
        new("continue", "Continue", "continue", "VS Code/JetBrains extension → config.yaml",
            "openai", new() { ["OPENAI_BASE_URL"] = "{baseUrl}/v1", ["OPENAI_API_KEY"] = "{apiKey}" }),
    ];

    /// <summary>Resolve {baseUrl}/{apiKey} placeholders into the tool's env map.</summary>
    public static Dictionary<string, string> EnvFor(CliTool t, string baseUrl, string apiKey) =>
        t.Env.ToDictionary(kv => kv.Key,
            kv => kv.Value.Replace("{baseUrl}", baseUrl).Replace("{apiKey}", apiKey));

    /// <summary>Shell-style export block for a tool.</summary>
    public static string ExportBlock(CliTool t, string baseUrl, string apiKey) =>
        string.Join('\n', EnvFor(t, baseUrl, apiKey).Select(kv => $"export {kv.Key}=\"{kv.Value}\""));

    /// <summary>True when the tool's binary resolves on PATH (server-side probe).</summary>
    public static bool Detected(CliTool t)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
            return path.Split(Path.PathSeparator).Any(dir =>
                exts.Any(e => File.Exists(Path.Combine(dir, t.Binary + e))));
        }
        catch { return false; }
    }
}
