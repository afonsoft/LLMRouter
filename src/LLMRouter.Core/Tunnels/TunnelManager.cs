using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Tunnels;

/// <summary>
/// SPEC-060: supervisores de tunnels — cloudflared (quick/named), ngrok
/// (authtoken + api :4040) e tailscale (serve/funnel). Cada backend expõe
/// {start,stop,status,logs,publicUrl} igual ao upstream.
/// </summary>
public static partial class TunnelManager
{
    /// <summary>Backend descriptor.</summary>
    public sealed record Backend(string Id, string Name, string Binary);

    /// <summary>Backends suportados.</summary>
    public static readonly Backend[] Backends =
    [
        new("cloudflared", "Cloudflare Tunnel", "cloudflared"),
        new("ngrok", "ngrok", "ngrok"),
        new("tailscale", "Tailscale", "tailscale"),
    ];

    private sealed class State
    {
        public Process? Proc;
        public readonly List<string> Logs = new(200);
        public string? PublicUrl;
        public string? Error;
        public void Log(string l)
        {
            Logs.Add($"{DateTime.UtcNow:HH:mm:ss} {l}");
            if (Logs.Count > 400) Logs.RemoveRange(0, Logs.Count - 400);
        }
    }

    private static readonly ConcurrentDictionary<string, State> States = new();
    private static State S(string id) => States.GetOrAdd(id, _ => new State());

    /// <summary>Binário override p/ testes (path completo ou nome no PATH).</summary>
    public static string BinaryFor(string id) =>
        Environment.GetEnvironmentVariable($"LLMROUTER_{id.ToUpperInvariant()}_BIN")
        ?? Backends.First(b => b.Id == id).Binary;

    /// <summary>Porta local do gateway que o tunnel expõe.</summary>
    public static int LocalPort { get; set; } = 8080;

    /// <summary>Args de start por backend (token/config vem do cfg persistido).</summary>
    public static string[] ArgsFor(string id, Dictionary<string, string> cfg) => id switch
    {
        "cloudflared" => cfg.TryGetValue("token", out var t) && t.Length > 0
            ? ["tunnel", "run", "--token", t]
            : ["tunnel", "--url", $"http://127.0.0.1:{LocalPort}"],
        "ngrok" => cfg.TryGetValue("authtoken", out var at) && at.Length > 0
            ? ["http", $"{LocalPort}", "--authtoken", at, "--log", "stdout"]
            : ["http", $"{LocalPort}", "--log", "stdout"],
        "tailscale" => cfg.TryGetValue("funnel", out var f) && f == "true"
            ? ["funnel", "--bg", $"{LocalPort}"]
            : ["serve", "--bg", $"{LocalPort}"],
        _ => [],
    };

    /// <summary>Extrai a URL pública de uma linha de log (regex por backend).</summary>
    public static string? ParsePublicUrl(string id, string line) => id switch
    {
        "cloudflared" => TryUrl().Match(line) is { Success: true } m && m.Value.Contains("trycloudflare") ? m.Value : null,
        "ngrok" => TryUrl().Match(line) is { Success: true } m2 && m2.Value.Contains("ngrok") ? m2.Value : null,
        "tailscale" => TryUrl().Match(line) is { Success: true } m3 && m3.Value.Contains("ts.net") ? m3.Value : null,
        _ => null,
    };

    /// <summary>Start: spawna o binário do backend e observa stdout/stderr p/ URL pública.</summary>
    public static async Task<(bool ok, string detail)> StartAsync(string id,
        Dictionary<string, string> cfg)
    {
        var st = S(id);
        if (st.Proc is { HasExited: false }) return (true, "already running");
        var args = ArgsFor(id, cfg);
        try
        {
            var psi = new ProcessStartInfo(BinaryFor(id), string.Join(' ', args.Select(Quote)))
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            var p = Process.Start(psi);
            if (p is null) return (false, "spawn failed");
            st.Proc = p; st.PublicUrl = null; st.Error = null;
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            void OnLine(object? s, DataReceivedEventArgs a)
            {
                if (a.Data is null) return;
                st.Log(a.Data);
                st.PublicUrl ??= ParsePublicUrl(id, a.Data);
            }
            p.OutputDataReceived += OnLine; p.ErrorDataReceived += OnLine;
            return (true, $"pid {p.Id}");
        }
        catch (Exception ex) { st.Error = ex.Message; return (false, ex.Message); }
        static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
    }

    /// <summary>Stop: mata o supervisor.</summary>
    public static Task<(bool ok, string detail)> StopAsync(string id)
    {
        var st = S(id);
        if (st.Proc is { HasExited: false })
            try { st.Proc.Kill(entireProcessTree: true); st.Log("stopped"); }
            catch (Exception ex) { return Task.FromResult((false, ex.Message)); }
        st.Proc = null; st.PublicUrl = null;
        return Task.FromResult((true, "stopped"));
    }

    /// <summary>ngrok tem API local :4040 — tenta ler tunnels via JSON quando sem URL no log.</summary>
    public static async Task<string?> NgrokApiUrlAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var j = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:4040/api/tunnels")).RootElement;
            foreach (var t in j.GetProperty("tunnels").EnumerateArray())
                if (t.TryGetProperty("public_url", out var u)) return u.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>Status de um backend.</summary>
    public static async Task<object> StatusAsync(string id)
    {
        var st = S(id);
        var running = st.Proc is { HasExited: false };
        var url = st.PublicUrl;
        if (id == "ngrok" && running && url is null) url = await NgrokApiUrlAsync();
        return new
        {
            id, name = Backends.First(b => b.Id == id).Name,
            running, publicUrl = url, error = st.Error,
            pid = st.Proc is { HasExited: false } ? st.Proc.Id : (int?)null,
        };
    }

    /// <summary>Status de todos os backends.</summary>
    public static async Task<List<object>> StatusAllAsync()
    {
        var list = new List<object>();
        foreach (var b in Backends) list.Add(await StatusAsync(b.Id));
        return list;
    }

    /// <summary>Tail dos logs.</summary>
    public static IReadOnlyList<string> Logs(string id) => S(id).Logs.ToArray();

    // ---- config persistida em settings.data.tunnels.{id} ----

    /// <summary>Lê cfg persistida do backend.</summary>
    public static async Task<Dictionary<string, string>> ConfigAsync(LlmRouterDbContext db, string id)
    {
        var s = await db.Settings.FindAsync(1);
        if (s is null) return [];
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Data) ?? [];
        if (d.TryGetValue("tunnels", out var tn) && tn.ValueKind == JsonValueKind.Object
            && tn.TryGetProperty(id, out var one) && one.ValueKind == JsonValueKind.Object)
            return JsonSerializer.Deserialize<Dictionary<string, string>>(one.GetRawText()) ?? [];
        return [];
    }

    /// <summary>Grava cfg do backend (token/authtoken/funnel).</summary>
    public static async Task SaveConfigAsync(LlmRouterDbContext db, string id, Dictionary<string, string> cfg)
    {
        var s = await db.Settings.FindAsync(1)
            ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Data) ?? [];
        var tn = d.TryGetValue("tunnels", out var t) && t.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(t.GetRawText()) ?? []
            : [];
        tn[id] = JsonSerializer.SerializeToElement(cfg);
        d["tunnels"] = JsonSerializer.SerializeToElement(tn);
        s.Data = JsonSerializer.Serialize(d);
        await db.SaveChangesAsync();
    }

    [GeneratedRegex(@"https?://[^\s""']+")]
    private static partial Regex TryUrl();
}
