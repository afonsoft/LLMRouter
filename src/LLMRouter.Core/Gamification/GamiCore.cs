using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Gamification;

/// <summary>SPEC-056: gamification persistente — pontos, badges, invites, federação, notificações, anomalias.</summary>
public static class GamiCore
{
    /// <summary>Badges e limiares (requests = eventos 'request').</summary>
    public static readonly (string Id, string Title, string Metric, long Threshold)[] Badges =
    {
        ("first-request", "Primeira requisição", "request", 1),
        ("centurion", "100 requisições", "request", 100),
        ("millennium", "1k requisições", "request", 1000),
        ("token-mover", "1M tokens", "token", 1_000_000),
        ("combo-creator", "Criou um combo", "combo", 1),
        ("streak-7", "7 dias seguidos", "streak", 7),
    };

    static string Now() => DateTime.UtcNow.ToString("O");

    /// <summary>Score atual de um ator (soma dos scoreEvents).</summary>
    public static async Task<long> ScoreAsync(LlmRouterDbContext db, string actor) =>
        await db.GamiItems.Where(g => g.Kind == "scoreEvent" && g.Actor == actor).SumAsync(g => (long?)g.Points) ?? 0;

    /// <summary>Registra evento de pontos; concede badges, detecta anomalias e notifica.</summary>
    public static async Task<JsonElement> EventAsync(LlmRouterDbContext db, string actor, long points, string reason, string metric = "request")
    {
        db.GamiItems.Add(new GamiItem { Kind = "scoreEvent", ItemKey = metric, Actor = actor, Points = points, Data = reason, At = Now() });
        var total = await ScoreAsync(db, actor) + points;
        if (points > 500)
            db.GamiItems.Add(new GamiItem { Kind = "anomaly", ItemKey = "score-jump", Actor = actor, Points = points, Data = $"salto anômalo +{points} ({reason})", At = Now() });
        var awarded = new List<string>();
        foreach (var b in Badges.Where(b => b.Metric == metric))
        {
            var count = (await db.GamiItems.Where(g => g.Kind == "scoreEvent" && g.ItemKey == b.Metric && g.Actor == actor).SumAsync(g => (long?)g.Points) ?? 0)
                + (metric == b.Metric ? points : 0);
            if (count >= b.Threshold && !await db.GamiItems.AnyAsync(g => g.Kind == "earned" && g.ItemKey == b.Id && g.Actor == actor))
            {
                db.GamiItems.Add(new GamiItem { Kind = "earned", ItemKey = b.Id, Actor = actor, Data = b.Title, At = Now() });
                db.GamiItems.Add(new GamiItem { Kind = "notification", ItemKey = "badge", Actor = actor, Data = $"Badge conquistado: {b.Title}", At = Now() });
                awarded.Add(b.Id);
            }
        }
        await db.SaveChangesAsync();
        return JsonSerializer.SerializeToElement(new { actor, added = points, score = total, level = total / 1000, awarded });
    }

    /// <summary>Badges conquistados do ator.</summary>
    public static async Task<object> EarnedAsync(LlmRouterDbContext db, string actor)
    {
        var ids = await db.GamiItems.Where(g => g.Kind == "earned" && g.Actor == actor).Select(g => g.ItemKey).ToListAsync();
        return new { actor, earned = ids, all = Badges.Select(b => new { b.Id, b.Title, earned = ids.Contains(b.Id) }) };
    }

    /// <summary>Cria convite; quem resgata ganha pontos.</summary>
    public static async Task<string> InviteCreateAsync(LlmRouterDbContext db, string actor)
    {
        var code = Guid.NewGuid().ToString("N")[..10];
        db.GamiItems.Add(new GamiItem { Kind = "invite", ItemKey = code, Actor = actor, Data = "open", At = Now() });
        await db.SaveChangesAsync();
        return code;
    }

    /// <summary>Resgata convite (+100 pts pro novo, +50 pro dono).</summary>
    public static async Task<bool> InviteRedeemAsync(LlmRouterDbContext db, string code, string actor)
    {
        var inv = await db.GamiItems.FirstOrDefaultAsync(g => g.Kind == "invite" && g.ItemKey == code && g.Data == "open");
        if (inv is null) return false;
        inv.Data = "redeemed";
        await EventAsync(db, actor, 100, $"invite {code}");
        if (inv.Actor is not null) await EventAsync(db, inv.Actor, 50, $"invite redeem por {actor}");
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Feed de notificações do ator.</summary>
    public static async Task<object> NotificationsAsync(LlmRouterDbContext db, string actor, int limit) =>
        new { notifications = await db.GamiItems.Where(g => g.Kind == "notification" && g.Actor == actor)
            .OrderByDescending(g => g.Id).Take(Math.Clamp(limit, 1, 200))
            .Select(g => new { g.Id, g.ItemKey, g.Data, g.At }).ToListAsync() };

    /// <summary>Leaderboard local + peers federados (best-effort).</summary>
    public static async Task<object> LeaderboardAsync(LlmRouterDbContext db, HttpClient http, CancellationToken ct)
    {
        var local = await db.GamiItems.Where(g => g.Kind == "scoreEvent")
            .GroupBy(g => g.Actor).Select(g => new { actor = g.Key, score = g.Sum(x => x.Points) })
            .OrderByDescending(x => x.score).Take(50).ToListAsync(ct);
        var peers = new List<object>();
        foreach (var srv in await db.GamiItems.Where(g => g.Kind == "server").ToListAsync(ct))
        {
            try
            {
                var s = await http.GetStringAsync($"{srv.ItemKey.TrimEnd('/')}/api/gamification/leaderboard", ct);
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.TryGetProperty("leaderboard", out var lb))
                    peers.Add(new { server = srv.ItemKey, top = lb.EnumerateArray().Take(3).Select(x => x.GetRawText()).ToArray() });
            }
            catch { peers.Add(new { server = srv.ItemKey, error = "unreachable" }); }
        }
        return new { leaderboard = local, federation = peers };
    }

    /// <summary>Registra peer da federação.</summary>
    public static async Task ServerAddAsync(LlmRouterDbContext db, string baseUrl)
    {
        if (!await db.GamiItems.AnyAsync(g => g.Kind == "server" && g.ItemKey == baseUrl))
            db.GamiItems.Add(new GamiItem { Kind = "server", ItemKey = baseUrl, At = Now() });
        await db.SaveChangesAsync();
    }

    /// <summary>Rotaciona id de federação local.</summary>
    public static string RotateFederationId() => Guid.NewGuid().ToString("N")[..16];

    /// <summary>Transfere pontos para um peer (deduz local, registra transfer).</summary>
    public static async Task<bool> TransferAsync(LlmRouterDbContext db, HttpClient http, string actor, string toServer, string toActor, long points, CancellationToken ct)
    {
        var score = await ScoreAsync(db, actor);
        if (score < points || points <= 0) return false;
        try
        {
            var res = await http.PostAsync($"{toServer.TrimEnd('/')}/api/gamification/event",
                new StringContent(JsonSerializer.Serialize(new { actor = toActor, points, reason = $"transfer de {actor}", metric = "transfer" }), Encoding.UTF8, "application/json"), ct);
            if (!res.IsSuccessStatusCode) return false;
        }
        catch { return false; }
        db.GamiItems.Add(new GamiItem { Kind = "scoreEvent", ItemKey = "transfer-out", Actor = actor, Points = -points, Data = $"para {toActor}@{toServer}", At = Now() });
        db.GamiItems.Add(new GamiItem { Kind = "transfer", ItemKey = toServer, Actor = actor, Points = points, Data = toActor, At = Now() });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Anomalias registradas.</summary>
    public static async Task<object> AnomaliesAsync(LlmRouterDbContext db) =>
        new { anomalies = await db.GamiItems.Where(g => g.Kind == "anomaly").OrderByDescending(g => g.Id).Take(100)
            .Select(g => new { g.Id, g.Actor, g.Points, g.Data, g.At }).ToListAsync() };
}
