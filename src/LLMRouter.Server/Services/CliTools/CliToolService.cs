using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;

namespace LLMRouter.Server.Services.CliTools;

/// <summary>
/// Definition of a supported local CLI tool — ported verbatim from OmniRoute's
/// <c>src/shared/services/cliRuntime.ts</c> CLI_TOOLS registry.
/// </summary>
public sealed record CliToolDef(
    string Id,
    string? DefaultCommand,
    string EnvBinKey,
    bool RequiresBinary,
    int HealthcheckTimeoutMs,
    string ConfigPath,
    string[] EnvTemplate);

/// <summary>
/// Detects local AI CLI tools, reads/writes their config files to point them at
/// the gateway, and manages backups — a port of OmniRoute's /api/cli-tools/*
/// surface (cliRuntime.ts + the per-tool *-settings routes + apply/backups).
///
/// Writes are gated by <c>LLMR_ALLOW_CONFIG_WRITES</c> (default true) and every
/// write creates a backup under <c>&lt;config-dir&gt;/.llmrouter.bak/</c> first.
/// <c>LLMR_CONFIG_HOME</c> overrides the home directory used to resolve config
/// paths (validated: absolute, inside home, no traversal).
/// </summary>
public sealed class CliToolService
{
    // Registry ported from cliRuntime.ts CLI_TOOLS (paths verbatim).
    public static readonly IReadOnlyList<CliToolDef> Tools =
    [
        new("claude", "claude", "CLI_CLAUDE_BIN", true, 4000, ".claude/settings.json",
            ["ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN"]),
        new("codex", "codex", "CLI_CODEX_BIN", true, 4000, ".codex/config.toml",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("droid", "droid", "CLI_DROID_BIN", true, 8000, ".factory/settings.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("openclaw", "openclaw", "CLI_OPENCLAW_BIN", true, 15000, ".openclaw/openclaw.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("cursor", "agent", "CLI_CURSOR_BIN", true, 15000, ".cursor/cli-config.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("windsurf", null, "CLI_WINDSURF_BIN", false, 4000, ".windsurf/settings.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("devin", "devin", "CLI_DEVIN_BIN", true, 12000,
            OperatingSystem.IsWindows() ? "AppData/Roaming/devin/config.json" : ".config/devin/config.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("zcode", "zcode", "ZCODE_BIN", true, 15000, ".zcode/config.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("cline", "cline", "CLI_CLINE_BIN", true, 12000, ".cline/data/globalState.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("kilo", "kilocode", "CLI_KILO_BIN", true, 15000, ".local/share/kilo/auth.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("continue", "cn", "CLI_CONTINUE_BIN", true, 15000, ".continue/config.yaml",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("opencode", "opencode", "CLI_OPENCODE_BIN", true, 15000, ".config/opencode/opencode.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("hermes", "hermes", "CLI_HERMES_BIN", false, 4000, ".config/hermes/config.json",
            ["LLMROUTER_BASE_URL", "LLMROUTER_API_KEY"]),
        new("hermes-agent", "hermes", "CLI_HERMES_BIN", false, 4000, ".hermes/config.yaml",
            ["LLMROUTER_BASE_URL", "LLMROUTER_API_KEY"]),
        new("qoder", "qodercli", "CLI_QODER_BIN", true, 12000, ".qoder/settings.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("qwen", "qwen", "CLI_QWEN_BIN", true, 12000, ".qwen/settings.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("aider", "aider", "CLI_AIDER_BIN", true, 12000, ".aider.conf.yml",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("goose", "goose", "CLI_GOOSE_BIN", true, 12000, ".config/goose/config.yaml",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("gemini", "gemini", "CLI_GEMINI_BIN", true, 15000, ".gemini/settings.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("forge", "forge", "CLI_FORGE_BIN", true, 8000, ".forge/config.toml",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("jcode", "jcode", "CLI_JCODE_BIN", true, 8000, ".jcode/config.toml",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("omp", "omp", "CLI_OMP_BIN", true, 8000, ".omp/agent/models.yml",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("letta", "letta", "CLI_LETTA_BIN", true, 8000, ".letta/lc-local-backend/providers/auth.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("codewhale", "codewhale", "CLI_CODEWHALE_BIN", true, 8000, ".codewhale/config.toml",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("smelt", "smelt", "CLI_SMELT_BIN", true, 8000, ".smelt/config.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("pi", "pi", "CLI_PI_BIN", true, 8000, ".pi/config.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
        new("crush", "crush", "CLI_CRUSH_BIN", true, 8000, ".config/crush/crush.json",
            ["OPENAI_BASE_URL", "OPENAI_API_KEY"]),
    ];

    private static readonly HashSet<string> KnownIds = new(
        Tools.Select(t => t.Id).Concat(Tools.Where(t => t.Id.EndsWith("-agent")).Select(t => t.Id[..^6])),
        StringComparer.OrdinalIgnoreCase);

    private readonly string _home;
    private readonly string _baseUrl;

    public CliToolService(string? homeOverride = null, string? baseUrl = null)
    {
        _home = ResolveHome(homeOverride);
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? Environment.GetEnvironmentVariable("LLMR_PUBLIC_URL") ?? "http://localhost:8080"
            : baseUrl.TrimEnd('/');
    }

    public string ConfigHome => _home;
    public string GatewayBaseUrl => _baseUrl;
    public static bool WritesAllowed =>
        !string.Equals(Environment.GetEnvironmentVariable("LLMR_ALLOW_CONFIG_WRITES"), "false",
            StringComparison.OrdinalIgnoreCase);

    public static CliToolDef? Find(string toolId) =>
        Tools.FirstOrDefault(t => t.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Validated home dir: CLI_CONFIG_HOME-style override, else user profile.</summary>
    private static string ResolveHome(string? overridePath)
    {
        var home = string.IsNullOrWhiteSpace(overridePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : overridePath;
        var envOverride = Environment.GetEnvironmentVariable("LLMR_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(envOverride) && Path.IsPathRooted(envOverride))
        {
            var norm = Path.GetFullPath(envOverride);
            var homeNorm = Path.GetFullPath(home);
            if (!norm.Contains("..", StringComparison.Ordinal) &&
                norm.StartsWith(homeNorm, StringComparison.Ordinal) &&
                !norm.Any(c => c is '<' or '>' or '|' or '?' or '*' or '"'))
            {
                return norm;
            }
        }
        return home;
    }

    public string ConfigPathFor(CliToolDef tool) => Path.Combine(_home, tool.ConfigPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Locate the tool binary: env override then PATH scan (ported from cliRuntime locateCommand).</summary>
    public string? LocateBinary(CliToolDef tool)
    {
        var envBin = Environment.GetEnvironmentVariable(tool.EnvBinKey);
        if (!string.IsNullOrWhiteSpace(envBin) && File.Exists(envBin))
            return envBin;
        if (tool.DefaultCommand is null)
            return null;
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, tool.DefaultCommand + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    /// <summary>Whether the tool's config already points at this gateway.</summary>
    public bool IsConfigured(CliToolDef tool, string? content = null)
    {
        content ??= TryRead(ConfigPathFor(tool));
        if (content is null) return false;
        if (content.Contains(_baseUrl, StringComparison.OrdinalIgnoreCase) ||
            content.Contains("llmrouter", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var v in tool.EnvTemplate)
            if (content.Contains(v, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static string? TryRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    public object StatusFor(CliToolDef tool)
    {
        var bin = LocateBinary(tool);
        var path = ConfigPathFor(tool);
        var exists = File.Exists(path);
        return new
        {
            installed = bin is not null || !tool.RequiresBinary,
            runnable = bin is not null,
            command = tool.DefaultCommand,
            commandPath = bin,
            configPath = path,
            configured = exists && IsConfigured(tool),
            configExists = exists,
            reason = bin is null && tool.RequiresBinary ? "binary_not_found" : null,
        };
    }

    public object ReadSettings(CliToolDef tool)
    {
        var path = ConfigPathFor(tool);
        var bin = LocateBinary(tool);
        if (bin is null && tool.RequiresBinary)
        {
            return new { installed = false, settings = (JsonNode?)null, settingsPath = path };
        }
        var text = TryRead(path);
        JsonNode? settings = null;
        if (text is not null && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try { settings = JsonNode.Parse(text); } catch { /* keep raw */ }
        }
        return new
        {
            installed = true,
            configured = IsConfigured(tool, text),
            settings = settings,
            rawSettings = settings is null ? text : null,
            settingsPath = path,
            commandPath = bin,
        };
    }

    /// <summary>Ensure a backup of <paramref name="path"/> exists; returns backup path or null.</summary>
    public string? EnsureBackup(string path)
    {
        if (!File.Exists(path)) return null;
        var dir = Path.Combine(Path.GetDirectoryName(path)!, ".llmrouter.bak");
        Directory.CreateDirectory(dir);
        var backup = Path.Combine(dir, Path.GetFileName(path) + ".bak");
        File.Copy(path, backup, overwrite: true);
        return backup;
    }

    /// <summary>
    /// Merge <paramref name="env"/> into the tool's settings file (the pattern of the
    /// upstream *-settings POST routes). For JSON files the vars land under the
    /// top-level <c>env</c> object; for TOML/YAML/text a managed
    /// <c># &gt;&gt;&gt; llmrouter</c> block is appended.
    /// </summary>
    public (int status, object body) ApplyEnv(CliToolDef tool, JsonObject env)
    {
        if (!WritesAllowed)
            return (403, new { error = "CLI config writes are disabled (LLMR_ALLOW_CONFIG_WRITES=false)" });
        var path = ConfigPathFor(tool);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backup = EnsureBackup(path);

        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            JsonObject root;
            try { root = JsonNode.Parse(TryRead(path) ?? "{}") as JsonObject ?? []; }
            catch { root = []; }
            var envObj = root["env"] as JsonObject ?? [];
            foreach (var kv in env)
                envObj[kv.Key] = kv.Value?.DeepClone();
            root["env"] = envObj;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            var existing = TryRead(path) ?? "";
            var managed = new System.Text.StringBuilder();
            managed.AppendLine("# >>> llmrouter");
            foreach (var kv in env)
                managed.AppendLine($"{kv.Key} = {kv.Value?.ToString()}");
            managed.Append("# <<< llmrouter");
            var stripped = System.Text.RegularExpressions.Regex.Replace(
                existing, "(?s)# >>> llmrouter.*?# <<< llmrouter", "").TrimEnd();
            File.WriteAllText(path, stripped + Environment.NewLine + managed);
        }
        return (200, new { ok = true, settingsPath = path, backupPath = backup });
    }

    /// <summary>Apply generated gateway config for a tool (port of cli-tools/apply POST).</summary>
    public (int status, object body) Apply(CliToolDef tool, string? apiKey, string? model, bool dryRun)
    {
        var env = new JsonObject
        {
            [tool.EnvTemplate[0]] = _baseUrl,
            [tool.EnvTemplate[1]] = apiKey ?? "",
        };
        if (!string.IsNullOrWhiteSpace(model) && tool.Id == "claude")
            env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = model;
        if (dryRun)
            return (200, new { dryRun = true, configPath = ConfigPathFor(tool), env });
        return ApplyEnv(tool, env);
    }

    /// <summary>List all .llmrouter.bak backups under the config home.</summary>
    public IEnumerable<object> ListBackups()
    {
        if (!Directory.Exists(_home)) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(_home, "*.bak", SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var f in files.Where(f => f.Contains(".llmrouter.bak", StringComparison.Ordinal)))
        {
            var info = new FileInfo(f);
            yield return new
            {
                backupPath = f,
                configPath = Path.Combine(
                    Path.GetDirectoryName(Path.GetDirectoryName(f))!, Path.GetFileNameWithoutExtension(f)),
                size = info.Length,
                createdAt = info.LastWriteTimeUtc,
            };
        }
    }
}
