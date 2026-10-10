using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Versioning;

/// <summary>
/// SPEC-059: version-manager — status/check-update/install/restart/shutdown.
/// Self-update baixa o asset do GitHub releases p/ staging e marca
/// pendingApply; restart/shutdown agenda exit p/ o supervisor religar.
/// </summary>
public static class VersionManager
{
    /// <summary>Versão do assembly (InformationalVersion ou Version).</summary>
    public static string CurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? asm.GetName().Version?.ToString() ?? "0.0.0";
    }

    /// <summary>SHA do commit (InformationalVersion suffix após '+', se houver).</summary>
    public static string? CommitSha()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return v?.Contains('+') == true ? v.Split('+')[1] : null;
    }

    /// <summary>Endpoint do feed de releases (override p/ teste).</summary>
    public static string ReleasesUrl { get; set; } =
        "https://api.github.com/repos/afonsoft/LLMRouter/releases/latest";

    /// <summary>Parse do payload do GitHub releases → (tag, url do release, body/changelog).</summary>
    public static (string tag, string releaseUrl, string changelog) ParseLatestRelease(string json)
    {
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        return (r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
            r.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "",
            r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "");
    }

    /// <summary>Consulta o feed e compara com a versão atual.</summary>
    public static async Task<object> CheckUpdateAsync(HttpClient http)
    {
        var (tag, url, changelog) = ("", "", "");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
            req.Headers.UserAgent.ParseAdd("LLMRouter");
            var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
                (tag, url, changelog) = ParseLatestRelease(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { return new { error = ex.Message, current = CurrentVersion() }; }
        var cur = CurrentVersion();
        return new
        {
            current = cur, latest = tag, releaseUrl = url,
            changelogUrl = url, changelogPreview = changelog[..Math.Min(500, changelog.Length)],
            updateAvailable = tag.TrimStart('v') != cur.TrimStart('v') && tag.Length > 0,
        };
    }

    /// <summary>SHA-256 hex de um arquivo.</summary>
    public static async Task<string> Sha256Async(string path)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs));
    }

    /// <summary>Verifica checksum; null/empty = sem verificação.</summary>
    public static async Task<bool> VerifySha256Async(string path, string? expected) =>
        expected is null or "" || string.Equals(await Sha256Async(path), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Baixa o asset p/ staging e marca pendingApply (checksum opcional).</summary>
    public static async Task<(bool ok, string detail)> InstallAsync(LlmRouterDbContext db,
        HttpClient http, string assetUrl, string? sha256 = null)
    {
        if (!assetUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            && !assetUrl.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
            return (false, "assetUrl deve ser um release do GitHub");
        var dir = Path.Combine(Path.GetTempPath(), "llmr-update");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(new Uri(assetUrl).LocalPath));
        try
        {
            var bytes = await http.GetByteArrayAsync(assetUrl);
            await File.WriteAllBytesAsync(dest, bytes);
        }
        catch (Exception ex) { return (false, ex.Message); }
        if (!await VerifySha256Async(dest, sha256)) { File.Delete(dest); return (false, "checksum mismatch"); }

        var s = await db.Settings.FindAsync(1)
            ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Data) ?? [];
        d["pendingApply"] = JsonSerializer.SerializeToElement(new { staged = dest, asset = assetUrl, at = DateTime.UtcNow.ToString("O") });
        s.Data = JsonSerializer.Serialize(d);
        await db.SaveChangesAsync();
        return (true, dest);
    }

    /// <summary>Quando true (prod), Exit é agendado de verdade; testes desligam.</summary>
    public static bool ExitOnRequest { get; set; } = true;

    /// <summary>Último pedido de exit (código + motivo) — observável p/ testes.</summary>
    public static (int code, string reason)? ExitRequested { get; private set; }

    /// <summary>Agenda shutdown/restart (exit após delay p/ resposta sair).</summary>
    public static void RequestExit(int code, string reason)
    {
        ExitRequested = (code, reason);
        if (!ExitOnRequest) return;
        _ = Task.Run(async () => { await Task.Delay(800); Environment.Exit(code); });
    }
}
