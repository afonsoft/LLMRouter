using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Bots;

/// <summary>SPEC-065: comandos de bots (telegram/copilot), tokens dahl, copilot stub.</summary>
public static class BotCore
{
    /// <summary>Comando → texto de resposta usando APIs internas.</summary>
    public static async Task<string> ReplyAsync(LlmRouterDbContext db, string text)
    {
        var cmd = text.Trim().Split(' ')[0].ToLowerInvariant();
        switch (cmd)
        {
            case "/status":
            case "status":
                var reqs = await db.UsageHistory.LongCountAsync();
                var keys = await db.ApiKeys.CountAsync();
                return $"LLMRouter ok — {reqs} requests, {keys} api keys.";
            case "/usage":
            case "usage":
                var prefix = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
                var today = await db.UsageHistory.Where(u => u.Timestamp.StartsWith(prefix)).LongCountAsync();
                return $"Hoje: {today} requests.";
            case "/providers":
            case "providers":
                var conns = await db.ProviderConnections.Select(p => p.Id).ToListAsync();
                return $"Providers: {(conns.Count == 0 ? "nenhum" : string.Join(", ", conns.Take(10)))}";
            default:
                return "Comandos: /status /usage /providers";
        }
    }

    /// <summary>Copilot stub: resolve por keywords e retorna traço de ferramentas.</summary>
    public static async Task<JsonElement> CopilotAsync(LlmRouterDbContext db, string message)
    {
        var tools = new List<string>();
        var parts = new List<string>();
        var m = message.ToLowerInvariant();
        if (m.Contains("status") || m.Contains("saude") || m.Contains("health"))
        { tools.Add("status.get"); parts.Add(await ReplyAsync(db, "status")); }
        if (m.Contains("usage") || m.Contains("uso") || m.Contains("request"))
        { tools.Add("usage.get"); parts.Add(await ReplyAsync(db, "usage")); }
        if (m.Contains("provider") || m.Contains("modelo") || m.Contains("model"))
        { tools.Add("providers.list"); parts.Add(await ReplyAsync(db, "providers")); }
        if (parts.Count == 0) parts.Add("Posso consultar status, uso e providers. Tente: 'qual o status?'");
        return JsonSerializer.SerializeToElement(new { answer = string.Join(" ", parts), tools });
    }

    /// <summary>Emite token compat dahl (opaco, guarda hash).</summary>
    public static async Task<string> DahlIssueAsync(LlmRouterDbContext db, string subject)
    {
        var token = "dahl_" + Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var row = await db.Kv.FindAsync("dahl", token);
        if (row is null) db.Kv.Add(new KvEntry { Scope = "dahl", Key = token, Value = JsonSerializer.Serialize(new { subject, hash, at = DateTime.UtcNow.ToString("O") }) });
        else row.Value = JsonSerializer.Serialize(new { subject, hash, at = DateTime.UtcNow.ToString("O") });
        await db.SaveChangesAsync();
        return token;
    }

    /// <summary>Verifica token dahl (hash confere e não expirado — 30d).</summary>
    public static async Task<bool> DahlVerifyAsync(LlmRouterDbContext db, string token)
    {
        var kv = await db.Kv.FindAsync("dahl", token);
        if (kv is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(kv.Value);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
            if (!doc.RootElement.TryGetProperty("hash", out var h) || h.GetString() != hash) return false;
            if (doc.RootElement.TryGetProperty("at", out var a) && DateTime.TryParse(a.GetString(), out var at) && DateTime.UtcNow - at > TimeSpan.FromDays(30)) return false;
            return true;
        }
        catch { return false; }
    }
}
