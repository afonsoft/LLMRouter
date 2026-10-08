using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;


namespace LLMRouter.Core.Extras;

/// <summary>SPEC-015: chaos fault injection + webhook dispatch + gamification + discovery.</summary>
public static class Extras
{
    /// <summary>Chaos config from kv 'chaos': {errorPct, latencyMs}.</summary>
    private static async Task<string?> KvGet(LlmRouterDbContext db, string scope, string key) =>
        (await db.Kv.FindAsync(scope, key))?.Value;

    private static async Task KvSet(LlmRouterDbContext db, string scope, string key, string value)
    {
        var row = await db.Kv.FindAsync(scope, key);
        if (row is null) db.Kv.Add(new KvEntry { Scope = scope, Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync();
    }

    public static async Task<int?> ChaosDelayAsync(LlmRouterDbContext db)
    {
        var raw = await KvGet(db, "chaos", "config");
        if (raw is null) return null;
        try
        {
            var e = JsonDocument.Parse(raw).RootElement;
            var latency = e.TryGetProperty("latencyMs", out var l) ? l.GetInt32() : 0;
            if (latency > 0) await Task.Delay(latency);
            var pct = e.TryGetProperty("errorPct", out var p) ? p.GetInt32() : 0;
            if (pct > 0 && Random.Shared.Next(100) < pct) return 503;
            return null;
        }
        catch { return null; }
    }

    /// <summary>Fire-and-forget POST to every webhook subscribed to <paramref name="evt"/>.</summary>
    public static async Task WebhooksDispatchAsync(LlmRouterDbContext db, HttpClient http, string evt, object payload)
    {
        var raw = await KvGet(db, "webhooks", "list");
        if (raw is null) return;
        try
        {
            foreach (var w in JsonDocument.Parse(raw).RootElement.EnumerateArray())
            {
                var url = w.GetProperty("url").GetString()!;
                var events = w.TryGetProperty("events", out var e)
                    ? e.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : ["*"];
                if (!events.Contains("*") && !events.Contains(evt)) continue;
                _ = Task.Run(async () =>
                {
                    try { await http.PostAsJsonAsync(url, new { evt, at = DateTime.UtcNow, data = payload }); }
                    catch { /* webhooks are best-effort */ }
                });
            }
        }
        catch { }
    }

    /// <summary>Audit log entry appended to kv 'audit' (JSON array, capped at 1000).</summary>
    public static async Task AuditAsync(LlmRouterDbContext db, string action, string detail)
    {
        var raw = await KvGet(db, "audit", "log");
        var list = raw is null ? [] : JsonSerializer.Deserialize<List<Dictionary<string, string>>>(raw) ?? [];
        list.Add(new() { ["at"] = DateTime.UtcNow.ToString("O"), ["action"] = action, ["detail"] = detail });
        while (list.Count > 1000) list.RemoveAt(0);
        await KvSet(db, "audit", "log", JsonSerializer.Serialize(list));
    }

    /// <summary>XP/badges from usage counters (requests, tokens, distinct providers).</summary>
    public static (int xp, string level, string[] badges) Gamification(long requests, long tokens, int providers)
    {
        var xp = (int)(requests * 10 + tokens / 100 + providers * 50);
        var level = xp switch { >= 50_000 => "grandmaster", >= 10_000 => "master", >= 2_500 => "expert", >= 500 => "adept", _ => "novice" };
        var badges = new List<string>();
        if (requests >= 1) badges.Add("first-request");
        if (requests >= 100) badges.Add("centurion");
        if (requests >= 1000) badges.Add("millennium");
        if (tokens >= 1_000_000) badges.Add("token-mover");
        if (providers >= 3) badges.Add("multi-provider");
        if (providers >= 10) badges.Add("provider-collector");
        return (xp, level, badges.ToArray());
    }

    /// <summary>Local provider discovery: probe well-known ports (Ollama, LM Studio, llama.cpp, vLLM, Jan).</summary>
    public static readonly (string Name, int Port, string HealthPath)[] DiscoveryTargets =
    [
        ("ollama", 11434, "/api/tags"),
        ("lmstudio", 1234, "/v1/models"),
        ("llamacpp", 8080, "/health"),
        ("vllm", 8000, "/v1/models"),
        ("jan", 1337, "/v1/models"),
    ];
}
