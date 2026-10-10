using System.Diagnostics;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Skills;

/// <summary>
/// SPEC-058: skills extras — executions, collect/{detect,chaos}, marketplace,
/// skillssh-style install. O store local é {contentRoot}/skills (SPEC-009/026).
/// </summary>
public static class SkillsExtras
{
    /// <summary>Parse frontmatter YAML-lite de SKILL.md (name/description/command/deps).</summary>
    public static Dictionary<string, string> Frontmatter(string path)
    {
        var map = new Dictionary<string, string>();
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || lines[0].Trim() != "---") return map;
        for (var i = 1; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Trim() == "---") break;
            var c = l.IndexOf(':');
            if (c > 0) map[l[..c].Trim()] = l[(c + 1)..].Trim().Trim('"');
        }
        return map;
    }

    /// <summary>Detecta dirs com SKILL.md sob as roots configuradas (settings.data.skills.roots).</summary>
    public static async Task<List<object>> DetectAsync(LlmRouterDbContext db, string storeDir,
        IEnumerable<string>? extraRoots = null)
    {
        var roots = new List<string> { storeDir };
        var s = await db.Settings.FindAsync(1);
        if (s is not null)
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Data) ?? [];
            if (d.TryGetValue("skills", out var sk) && sk.TryGetProperty("roots", out var r))
                roots.AddRange(JsonSerializer.Deserialize<string[]>(r.GetRawText()) ?? []);
        }
        if (extraRoots is not null) roots.AddRange(extraRoots);

        var found = new List<object>();
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories))
            {
                var fm = Frontmatter(f);
                var dir = Path.GetDirectoryName(f)!;
                found.Add(new
                {
                    dir,
                    name = fm.TryGetValue("name", out var n) ? n : Path.GetFileName(dir),
                    description = fm.TryGetValue("description", out var d2) ? d2 : "",
                    inStore = dir.StartsWith(Path.GetFullPath(storeDir)),
                });
            }
        return found;
    }

    /// <summary>Copia um dir com SKILL.md p/ o store (collect).</summary>
    public static (bool ok, string detail) Collect(string srcDir, string storeDir)
    {
        if (!File.Exists(Path.Combine(srcDir, "SKILL.md")))
            return (false, "no SKILL.md");
        var dest = Path.Combine(storeDir, Path.GetFileName(srcDir.TrimEnd('/', '\\')));
        if (Directory.Exists(dest)) return (false, "already in store");
        CopyDir(srcDir, dest);
        return (true, dest);
        static void CopyDir(string s, string t)
        {
            Directory.CreateDirectory(t);
            foreach (var f in Directory.GetFiles(s)) File.Copy(f, Path.Combine(t, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(s)) CopyDir(d, Path.Combine(t, Path.GetFileName(d)));
        }
    }

    /// <summary>Chaos: skills cujos deps declarados (deps:) não estão no PATH.</summary>
    public static List<object> Chaos(string storeDir)
    {
        var missing = new List<object>();
        if (!Directory.Exists(storeDir)) return missing;
        foreach (var f in Directory.EnumerateFiles(storeDir, "SKILL.md", SearchOption.AllDirectories))
        {
            var fm = Frontmatter(f);
            if (!fm.TryGetValue("deps", out var deps)) continue;
            var absent = deps.Split(',', ' ').Where(d => d.Length > 0)
                .Where(d => FindOnPath(d) is null).ToArray();
            if (absent.Length > 0)
                missing.Add(new { skill = fm.GetValueOrDefault("name", Path.GetFileName(Path.GetDirectoryName(f)!)), missing = absent });
        }
        return missing;
    }

    private static string? FindOnPath(string bin)
    {
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (File.Exists(Path.Combine(p, bin))) return p;
        return null;
    }

    /// <summary>Executa o command declarado no SKILL.md num sandbox dir e grava execução.</summary>
    public static async Task<(bool ok, string detail)> RunAsync(LlmRouterDbContext db,
        string storeDir, string skillId, int timeoutSec = 60)
    {
        var dir = Path.Combine(storeDir, skillId);
        var md = Path.Combine(dir, "SKILL.md");
        if (!File.Exists(md)) return (false, "skill not found");
        var fm = Frontmatter(md);
        if (!fm.TryGetValue("command", out var cmd) || cmd.Length == 0)
            return (false, "no command declared");

        var sandbox = Path.Combine(Path.GetTempPath(), $"skill-{skillId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandbox);
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo("/bin/bash", $"-c \"{cmd.Replace("\"", "\\\"")}\"")
        {
            WorkingDirectory = sandbox,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        string stdout, stderr; int code;
        try
        {
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutSec * 1000)) { p.Kill(); code = -1; stderr = "timeout"; stdout = await so; }
            else { code = p.ExitCode; stdout = await so; stderr = await se; }
        }
        catch (Exception ex) { return (false, ex.Message); }

        db.SkillExecutions.Add(new SkillExecution
        {
            Skill = skillId, Command = cmd, ExitCode = code,
            Stdout = stdout[..Math.Min(8000, stdout.Length)],
            Stderr = stderr[..Math.Min(4000, stderr.Length)],
            DurationMs = sw.ElapsedMilliseconds, At = DateTime.UtcNow.ToString("O"),
        });
        await db.SaveChangesAsync();
        return (true, $"exit {code} in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>Marketplace: busca index remoto (JSON {skills:[{id,name,description,url}]}).</summary>
    public static async Task<List<Dictionary<string, JsonElement>>> MarketplaceAsync(
        LlmRouterDbContext db, HttpClient http)
    {
        var urls = await MarketUrlsAsync(db);
        var all = new List<Dictionary<string, JsonElement>>();
        foreach (var u in urls)
            try
            {
                using var d = JsonDocument.Parse(await http.GetStringAsync(u));
                if (d.RootElement.TryGetProperty("skills", out var arr))
                    foreach (var s in arr.EnumerateArray())
                        all.Add(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.GetRawText())!);
            }
            catch { }
        return all;
    }

    /// <summary>Instala uma skill remota: baixa o SKILL.md (e tree simples) p/ o store.</summary>
    public static async Task<(bool ok, string detail)> MarketInstallAsync(
        HttpClient http, string storeDir, string id, string skillMdUrl)
    {
        var dest = Path.Combine(storeDir, id);
        Directory.CreateDirectory(dest);
        try
        {
            var md = await http.GetStringAsync(skillMdUrl);
            await File.WriteAllTextAsync(Path.Combine(dest, "SKILL.md"), md);
            return (true, dest);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static async Task<string[]> MarketUrlsAsync(LlmRouterDbContext db)
    {
        var s = await db.Settings.FindAsync(1);
        if (s is null) return [];
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Data) ?? [];
        return d.TryGetValue("skills", out var sk) && sk.TryGetProperty("marketplace", out var m)
            ? JsonSerializer.Deserialize<string[]>(m.GetRawText()) ?? [] : [];
    }
}
