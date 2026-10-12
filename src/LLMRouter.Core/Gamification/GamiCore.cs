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
        // SPEC-129: score events also feed upstream user_levels + global leaderboard + streak
        await AddXpAsync(db, actor, points);
        await UpdateScoreAsync(db, actor, "global", points);
        await TouchStreakAsync(db, actor);
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

    // ---- SPEC-129: upstream user_levels + leaderboard + streaks + federation auth ----

    /// <summary>XP needed to gain `level` (0 for level 1) — upstream xp.ts verbatim.</summary>
    public static int XpForLevel(int level) =>
        level <= 1 ? 0 : (int)Math.Floor(100 * Math.Pow(level, 1.5));

    public static long CumulativeXpForLevel(int level)
    {
        long total = 0;
        for (var i = 2; i <= level; i++) total += XpForLevel(i);
        return total;
    }

    /// <summary>Inverse-curve estimate reconciled against floored thresholds.</summary>
    public static int CalculateLevel(long totalXp)
    {
        if (totalXp <= 0) return 1;
        var level = Math.Max(1, (int)Math.Floor(Math.Pow((totalXp * 2.5) / 100, 0.4)));
        while (level > 1 && CumulativeXpForLevel(level) > totalXp) level--;
        while (CumulativeXpForLevel(level + 1) <= totalXp) level++;
        return level;
    }

    /// <summary>user_levels upsert — adds xp and recomputes the level.</summary>
    public static async Task AddXpAsync(LlmRouterDbContext db, string apiKeyId, long points, CancellationToken ct = default)
    {
        var row = await db.UserLevels.FindAsync([apiKeyId], ct);
        if (row is null)
        {
            row = new UserLevel { ApiKeyId = apiKeyId };
            db.UserLevels.Add(row);
        }
        row.TotalXp += points;
        row.CurrentLevel = CalculateLevel(row.TotalXp);
        row.UpdatedAt = Now();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>leaderboard upsert — scope-bucketed score accumulation.</summary>
    public static async Task UpdateScoreAsync(LlmRouterDbContext db, string apiKeyId, string scope, long points, CancellationToken ct = default)
    {
        var row = await db.LeaderboardScores.FindAsync([apiKeyId, scope], ct);
        if (row is null)
        {
            row = new LeaderboardScore { ApiKeyId = apiKeyId, Scope = scope };
            db.LeaderboardScores.Add(row);
        }
        row.Score += points;
        row.UpdatedAt = Now();
        await db.SaveChangesAsync(ct);
    }

    public const int LeaderboardMaxLimit = 200;

    /// <summary>getTopN — scope leaderboard ordered by score desc (limit clamped ≤200).</summary>
    public static async Task<List<object>> TopNAsync(LlmRouterDbContext db, string scope, int limit, int offset, CancellationToken ct = default)
    {
        var safeLimit = Math.Min(Math.Max(limit, 0), LeaderboardMaxLimit);
        var safeOffset = Math.Max(offset, 0);
        return await db.LeaderboardScores.Where(s => s.Scope == scope)
            .OrderByDescending(s => s.Score)
            .Skip(safeOffset).Take(safeLimit)
            .Select(s => (object)new { apiKeyId = s.ApiKeyId, scope = s.Scope, score = s.Score, updatedAt = s.UpdatedAt })
            .ToListAsync(ct);
    }

    /// <summary>kv namespace for per-key streak JSON (upstream key_value/streaks).</summary>
    public const string StreakScope = "streaks";

    public record StreakData(long CurrentStreak, long LongestStreak, string LastActiveDate, string StreakStartDate);

    public static async Task<StreakData> GetStreakAsync(LlmRouterDbContext db, string apiKeyId, CancellationToken ct = default)
    {
        var row = await db.Kv.FindAsync([StreakScope, apiKeyId], ct);
        if (row?.Value is not { } v) return new StreakData(0, 0, "", "");
        try
        {
            var j = JsonSerializer.Deserialize<JsonElement>(v);
            return new StreakData(
                j.TryGetProperty("currentStreak", out var c) ? c.GetInt64() : 0,
                j.TryGetProperty("longestStreak", out var l) ? l.GetInt64() : 0,
                j.TryGetProperty("lastActiveDate", out var la) ? la.GetString() ?? "" : "",
                j.TryGetProperty("streakStartDate", out var ss) ? ss.GetString() ?? "" : "");
        }
        catch (JsonException) { return new StreakData(0, 0, "", ""); }
    }

    /// <summary>Rolling-day streak bump on activity (upstream updateStreak semantics).</summary>
    public static async Task TouchStreakAsync(LlmRouterDbContext db, string apiKeyId, CancellationToken ct = default)
    {
        var s = await GetStreakAsync(db, apiKeyId, ct);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        var current = s.LastActiveDate == today ? s.CurrentStreak
            : s.LastActiveDate == yesterday ? s.CurrentStreak + 1 : 1;
        var start = s.LastActiveDate == today ? s.StreakStartDate
            : s.LastActiveDate == yesterday ? s.StreakStartDate : today;
        var updated = new
        {
            currentStreak = current,
            longestStreak = Math.Max(s.LongestStreak, current),
            lastActiveDate = today,
            streakStartDate = string.IsNullOrEmpty(start) ? today : start,
        };
        var val = JsonSerializer.Serialize(updated);
        var row = await db.Kv.FindAsync([StreakScope, apiKeyId], ct);
        if (row is null) db.Kv.Add(new KvEntry { Scope = StreakScope, Key = apiKeyId, Value = val });
        else row.Value = val;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Aggregate streak — max current/longest over every key (upstream).</summary>
    public static async Task<(long Current, long Longest)> AggregateStreakAsync(LlmRouterDbContext db, CancellationToken ct = default)
    {
        var rows = await db.Kv.Where(k => k.Scope == StreakScope).Select(k => k.Value).ToListAsync(ct);
        long current = 0, longest = 0;
        foreach (var v in rows)
        {
            try
            {
                var j = JsonSerializer.Deserialize<JsonElement>(v);
                if (j.TryGetProperty("currentStreak", out var c)) current = Math.Max(current, c.GetInt64());
                if (j.TryGetProperty("longestStreak", out var l)) longest = Math.Max(longest, l.GetInt64());
            }
            catch (JsonException) { /* malformed rows count as zero (upstream) */ }
        }
        return (current, longest);
    }

    /// <summary>GET /api/gamification/level payload — per-key or aggregate.</summary>
    public static async Task<object> LevelAsync(LlmRouterDbContext db, string? apiKeyId, CancellationToken ct = default)
    {
        object level;
        long current, longest;
        if (apiKeyId is { } key)
        {
            var row = await db.UserLevels.FindAsync([key], ct);
            level = row is null ? (object)new { apiKeyId = key, totalXp = 0, currentLevel = 1, updatedAt = (string?)null }
                : new { apiKeyId = row.ApiKeyId, totalXp = row.TotalXp, currentLevel = row.CurrentLevel, updatedAt = (string?)row.UpdatedAt };
            var s = await GetStreakAsync(db, key, ct);
            (current, longest) = (s.CurrentStreak, s.LongestStreak);
        }
        else
        {
            var totalXp = await db.UserLevels.SumAsync(u => (long?)u.TotalXp, ct) ?? 0;
            var updatedAt = await db.UserLevels.MaxAsync(u => (string?)u.UpdatedAt, ct);
            level = new { apiKeyId = "aggregate", totalXp, currentLevel = CalculateLevel(totalXp), updatedAt };
            (current, longest) = await AggregateStreakAsync(db, ct);
        }
        return new { level, streak = new { current, longest } };
    }

    /// <summary>getBadges / getAllEarnedBadges — earned badges per key or distinct set.</summary>
    public static async Task<object> EarnedBadgesAsync(LlmRouterDbContext db, string? apiKeyId, CancellationToken ct = default)
    {
        var q = db.GamiItems.Where(g => g.Kind == "earned");
        if (apiKeyId is { } key) q = q.Where(g => g.Actor == key);
        var items = await q.Select(g => g.ItemKey).Distinct().ToListAsync(ct);
        return new { badges = items };
    }

    /// <summary>PBKDF2-SHA256 federation token hash (upstream pbkdf2 params).</summary>
    public static string FederationHash(string token) =>
        Convert.ToHexString(System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes("omniroute-federation-salt"),
            120000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32)).ToLowerInvariant();

    /// <summary>Authenticate a federation caller: Bearer token hash must match a
    /// connected server peer (kind=server item whose Data.keyHash matches).</summary>
    public static async Task<GamiItem?> FederationAuthAsync(LlmRouterDbContext db, string? authHeader, CancellationToken ct = default)
    {
        if (authHeader is null || !authHeader.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var hash = FederationHash(authHeader[7..]);
        var peers = await db.GamiItems.Where(g => g.Kind == "server").ToListAsync(ct);
        return peers.FirstOrDefault(g =>
        {
            try
            {
                var d = JsonSerializer.Deserialize<JsonElement>(g.Data ?? "{}");
                return d.TryGetProperty("keyHash", out var h) && h.GetString() == hash
                    && (!d.TryGetProperty("status", out var s) || s.GetString() == "connected");
            }
            catch (JsonException) { return false; }
        });
    }

    /// <summary>Registra peer da federação com hash de autenticação opcional.</summary>
    public static async Task ServerAddAsync(LlmRouterDbContext db, string baseUrl, string? apiKey = null)
    {
        var peer = await db.GamiItems.FirstOrDefaultAsync(g => g.Kind == "server" && g.ItemKey == baseUrl);
        var data = JsonSerializer.Serialize(new
        {
            baseUrl,
            keyHash = apiKey is null ? null : FederationHash(apiKey),
            status = "connected",
        });
        if (peer is null) db.GamiItems.Add(new GamiItem { Kind = "server", ItemKey = baseUrl, Data = data, At = Now() });
        else if (apiKey is not null) peer.Data = data;
        await db.SaveChangesAsync();
    }
}
