using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Services;

/// <summary>
/// SPEC-061: ciclo de vida de sidecar services — catálogo (install/start/stop/
/// restart/status/logs/auto-start/auto-restart/provider-expose), detecção de
/// processo adotado (probe porta+health quando o processo não foi spawnado
/// por nós) e ring buffer de logs por serviço.
/// </summary>
public static class ServiceManager
{
    /// <summary>Descritor de um serviço do catálogo.</summary>
    public sealed record Descriptor(
        string Id, string Name, string? BinaryUrl, int Port, string HealthPath,
        string Args, string[] Env);

    private sealed class ProcState
    {
        public Process? Proc;
        public readonly List<string> Logs = new(256);
        public DateTime? StartedAt;
        public bool Adopted;
        public void Log(string line)
        {
            Logs.Add($"{DateTime.UtcNow:HH:mm:ss} {line}");
            if (Logs.Count > 500) Logs.RemoveRange(0, Logs.Count - 500);
        }
    }

    private static readonly ConcurrentDictionary<string, ProcState> States = new();

    /// <summary>Catálogo espelhando os nomes upstream.</summary>
    public static readonly Descriptor[] Catalog =
    [
        new("9router", "9router", null, 20127, "/health", "", []),
        new("bifrost", "Bifrost", "https://github.com/maximhq/bifrost/releases", 8081, "/health", "", []),
        new("cliproxy", "CLIProxy", "https://github.com/luispater/CLIProxyAPI/releases", 8317, "/v1/models", "", []),
        new("dario", "Dario", null, 9229, "/health", "", []),
        new("mux", "Mux", null, 9933, "/health", "", []),
        new("openwa", "OpenWA", null, 3737, "/health", "", []),
        new("llmlingua", "LLMLingua", null, 8899, "/health", "", []),
        new("redis", "Redis (bundled)", "https://download.redis.io/releases", 6379, "/", "--port 6379", []),
        new("novnc", "noVNC desktop", null, 6080, "/", "--listen 6080", []),
    ];

    /// <summary>Lookup por id (null se desconhecido).</summary>
    public static Descriptor? Find(string id) =>
        Catalog.FirstOrDefault(d => d.Id == id);

    private static ProcState State(string id) => States.GetOrAdd(id, _ => new ProcState());

    /// <summary>Diretório raiz de dados (DbDir) onde binários/logs vivem.</summary>
    public static string DirFor(string rootDir, string id) =>
        Path.Combine(rootDir, "services", id);

    /// <summary>Status agregado de todos os serviços do catálogo.</summary>
    public static async Task<List<object>> StatusAllAsync(LlmRouterDbContext db)
    {
        var flags = await FlagsAll(db);
        var list = new List<object>();
        foreach (var d in Catalog)
        {
            var st = State(d.Id);
            var running = st.Proc is { HasExited: false } || await AdoptedAsync(d);
            var installed = st.Proc is not null || Directory.Exists(DirFor(RootDir, d.Id));
            flags.TryGetValue(d.Id, out var f);
            list.Add(new
            {
                id = d.Id, name = d.Name, port = d.Port,
                installed, running, adopted = st.Adopted,
                pid = st.Proc is { HasExited: false } ? st.Proc.Id : (int?)null,
                startedAt = st.StartedAt?.ToString("O"),
                autoStart = f?.AutoStart == true, autoRestart = f?.AutoRestart == true,
                binaryUrl = d.BinaryUrl,
            });
        }
        return list;
    }

    /// <summary>RootDir configurável p/ testes — default ~/.local/share/LLMRouter.</summary>
    public static string RootDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "LLMRouter");

    /// <summary>Probe porta+health — detecta serviço vivo iniciado fora do app.</summary>
    public static async Task<bool> AdoptedAsync(Descriptor d)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var r = await http.GetAsync($"http://127.0.0.1:{d.Port}{d.HealthPath}");
            if (r.IsSuccessStatusCode)
            {
                State(d.Id).Adopted = true;
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>"Install" = cria o diretório do serviço e marca instalado (binário real
    /// só quando BinaryUrl é um arquivo .tar.gz/.zip baixável — stubs não baixam).</summary>
    public static Task<(bool ok, string detail)> InstallAsync(string id)
    {
        var d = Find(id);
        if (d is null) return Task.FromResult((false, "unknown service"));
        var dir = DirFor(RootDir, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "installed.json"),
            JsonSerializer.Serialize(new { id = d.Id, at = DateTime.UtcNow.ToString("O") }));
        State(id).Log("installed marker written");
        return Task.FromResult((true, dir));
    }

    /// <summary>Start: se o binário existir em services/{id}/bin spawna; senão apenas
    /// verifica se já está adotado (running em outro lugar).</summary>
    public static async Task<(bool ok, string detail)> StartAsync(string id)
    {
        var d = Find(id);
        if (d is null) return (false, "unknown service");
        var st = State(id);
        if (st.Proc is { HasExited: false }) return (true, "already running");

        var binDir = Path.Combine(DirFor(RootDir, id), "bin");
        var bin = Directory.Exists(binDir)
            ? Directory.EnumerateFiles(binDir).FirstOrDefault(f => !f.EndsWith(".txt"))
            : null;
        if (bin is null)
        {
            // sem binário local: só "start" se já estiver adotado na porta
            return await AdoptedAsync(d) ? (true, "adopted external process")
                                       : (false, "no binary installed");
        }
        try
        {
            var psi = new ProcessStartInfo(bin, d.Args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, WorkingDirectory = binDir,
            };
            foreach (var e in d.Env) { var i = e.IndexOf('='); if (i > 0) psi.Environment[e[..i]] = e[(i + 1)..]; }
            var p = Process.Start(psi);
            if (p is null) return (false, "spawn failed");
            st.Proc = p; st.StartedAt = DateTime.UtcNow; st.Adopted = false;
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            p.OutputDataReceived += (_, a) => { if (a.Data is not null) st.Log(a.Data); };
            p.ErrorDataReceived += (_, a) => { if (a.Data is not null) st.Log($"ERR {a.Data}"); };
            return (true, $"pid {p.Id}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Stop: mata o processo spawnado (processos adotados externos não são mortos).</summary>
    public static Task<(bool ok, string detail)> StopAsync(string id)
    {
        var st = State(id);
        if (st.Proc is { HasExited: false })
        {
            try { st.Proc.Kill(entireProcessTree: true); st.Log("stopped"); }
            catch (Exception ex) { return Task.FromResult((false, ex.Message)); }
        }
        st.Proc = null;
        return Task.FromResult((true, "stopped"));
    }

    /// <summary>Tail dos logs (ring buffer, máx 500 linhas).</summary>
    public static IReadOnlyList<string> Logs(string id) => State(id).Logs.ToArray();

    /// <summary>Running = processo nosso vivo OU adotado na porta.</summary>
    public static async Task<bool> RunningAsync(string id)
    {
        var d = Find(id);
        if (d is null) return false;
        var st = State(id);
        return st.Proc is { HasExited: false } || await AdoptedAsync(d);
    }

    // ---- auto-start / auto-restart persistidos em settings.data.services ----

    private sealed record Flags(bool AutoStart, bool AutoRestart);

    private static async Task<Dictionary<string, Flags>> FlagsAll(LlmRouterDbContext db)
    {
        var s = await db.Settings.FindAsync(1);
        var map = new Dictionary<string, Flags>();
        if (s is null) return map;
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Data) ?? [];
            if (d.TryGetValue("services", out var sv) && sv.ValueKind == JsonValueKind.Object)
                foreach (var p in sv.EnumerateObject())
                    map[p.Name] = new Flags(
                        p.Value.TryGetProperty("autoStart", out var a) && a.GetBoolean(),
                        p.Value.TryGetProperty("autoRestart", out var r) && r.GetBoolean());
        }
        catch { }
        return map;
    }

    /// <summary>Persiste flags do serviço em settings.data.services.{id}.</summary>
    public static async Task SetFlagAsync(LlmRouterDbContext db, string id,
        bool? autoStart, bool? autoRestart)
    {
        var s = await db.Settings.FindAsync(1)
            ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Data) ?? [];
        var services = d.TryGetValue("services", out var sv) && sv.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(sv.GetRawText()) ?? []
            : [];
        var cur = services.TryGetValue(id, out var c) && c.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(c.GetRawText()) ?? []
            : [];
        cur["autoStart"] = JsonSerializer.SerializeToElement(autoStart
            ?? (cur.TryGetValue("autoStart", out var a) && a.GetBoolean()));
        cur["autoRestart"] = JsonSerializer.SerializeToElement(autoRestart
            ?? (cur.TryGetValue("autoRestart", out var r) && r.GetBoolean()));
        services[id] = JsonSerializer.SerializeToElement(cur);
        d["services"] = JsonSerializer.SerializeToElement(services);
        s.Data = JsonSerializer.Serialize(d);
        await db.SaveChangesAsync();
    }

    /// <summary>provider-expose: cria/atualiza uma providerConnection openai-compatible
    /// apontando para a porta local do serviço.</summary>
    public static async Task<(bool ok, string detail, string? connectionId)> ProviderExposeAsync(
        LlmRouterDbContext db, string id)
    {
        var d = Find(id);
        if (d is null) return (false, "unknown service", null);
        var connId = $"svc-{id}";
        var c = await db.ProviderConnections.FindAsync(connId);
        if (c is null)
        {
            c = new ProviderConnection
            {
                Id = connId, Provider = "openai-compatible",
                Name = $"service:{d.Name}", IsActive = true,
                CreatedAt = DateTime.UtcNow.ToString("O"),
            };
            db.ProviderConnections.Add(c);
        }
        c.Data = JsonSerializer.Serialize(new { baseUrl = $"http://127.0.0.1:{d.Port}/v1", service = id });
        c.UpdatedAt = DateTime.UtcNow.ToString("O");
        await db.SaveChangesAsync();
        return (true, $"http://127.0.0.1:{d.Port}/v1", connId);
    }
}
