using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gamification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>
/// SPEC-129: a2a persisted history + gamification depth — user_levels XP curve,
/// leaderboard scopes, streaks, federation bearer auth (pbkdf2 keyHash),
/// badges/earned, tasks/history filters + upstream state names.
/// </summary>
public class Spec129A2aGamiDepthTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec129A2aGamiDepthTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
    }

    public void Dispose()
    {
        _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db() => new(new DbContextOptionsBuilder<LlmRouterDbContext>()
        .UseSqlite($"Data Source={_dbPath}").Options);

    private HttpClient Dash()
    {
        var c = _factory.CreateClient();
        c.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Result
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return c;
    }

    [Fact]
    public void CalculateLevel_MatchesUpstreamCurve()
    {
        GamiCore.CalculateLevel(0).ShouldBe(1);
        GamiCore.XpForLevel(1).ShouldBe(0);
        GamiCore.XpForLevel(10).ShouldBe((int)Math.Floor(100 * Math.Pow(10, 1.5)));
        GamiCore.CalculateLevel(GamiCore.CumulativeXpForLevel(10)).ShouldBe(10);
        GamiCore.CalculateLevel(GamiCore.CumulativeXpForLevel(10) + 1).ShouldBe(10);
    }

    [Fact]
    public async Task Event_FeedsXpLeaderboardStreak()
    {
        var c = Dash();
        (await c.PostAsJsonAsync("/api/gamification/event",
            new { actor = "k1", points = 150, reason = "t", metric = "request" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        using var db = Db();
        var lvl = await db.UserLevels.FindAsync("k1");
        lvl.ShouldNotBeNull();
        lvl.TotalXp.ShouldBe(150);
        lvl.CurrentLevel.ShouldBe(GamiCore.CalculateLevel(150));
        var lb = await db.LeaderboardScores.FindAsync("k1", "global");
        lb.ShouldNotBeNull();
        lb.Score.ShouldBe(150);
        var streak = await GamiCore.GetStreakAsync(db, "k1");
        streak.CurrentStreak.ShouldBe(1);
    }

    [Fact]
    public async Task Level_PerKeyAndAggregate()
    {
        var c = Dash();
        await c.PostAsJsonAsync("/api/gamification/event", new { actor = "k1", points = 150 });
        await c.PostAsJsonAsync("/api/gamification/event", new { actor = "k2", points = 50 });
        var r = await c.GetAsync("/api/gamification/level?apiKeyId=k1");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("level").GetProperty("totalXp").GetInt64().ShouldBe(150);
        doc.RootElement.GetProperty("streak").GetProperty("current").GetInt64().ShouldBe(1);

        var agg = await c.GetAsync("/api/gamification/level");
        using var d2 = JsonDocument.Parse(await agg.Content.ReadAsStringAsync());
        d2.RootElement.GetProperty("level").GetProperty("apiKeyId").GetString().ShouldBe("aggregate");
        d2.RootElement.GetProperty("level").GetProperty("totalXp").GetInt64().ShouldBe(200);
    }

    [Fact]
    public async Task FederationLeaderboard_BearerGateAndClamp()
    {
        var c = Dash();
        (await c.GetAsync("/api/gamification/federation/leaderboard"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var bad = new HttpRequestMessage(HttpMethod.Get, "/api/gamification/federation/leaderboard");
        bad.Headers.Add("Authorization", "Bearer wrong");
        (await c.SendAsync(bad)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // register peer with a federation apiKey → bearer validates by keyHash
        (await c.PostAsJsonAsync("/api/gamification/servers",
            new { url = "https://peer.local", apiKey = "fed-secret-1" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await c.PostAsJsonAsync("/api/gamification/event", new { actor = "k1", points = 99 });
        var ok = new HttpRequestMessage(HttpMethod.Get, "/api/gamification/federation/leaderboard?limit=1");
        ok.Headers.Add("Authorization", "Bearer fed-secret-1");
        var r = await c.SendAsync(ok);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var entries = doc.RootElement.GetProperty("entries");
        entries.GetArrayLength().ShouldBe(1);
        entries[0].GetProperty("apiKeyId").GetString().ShouldBe("k1");

        var clamp = new HttpRequestMessage(HttpMethod.Get, "/api/gamification/federation/leaderboard?limit=999");
        clamp.Headers.Add("Authorization", "Bearer fed-secret-1");
        (await c.SendAsync(clamp)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task FederationScore_WritesLeaderboardRow()
    {
        var c = Dash();
        await c.PostAsJsonAsync("/api/gamification/servers", new { url = "https://peer2", apiKey = "fed-2" });
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/gamification/federation/score")
        {
            Content = JsonContent.Create(new { apiKeyId = "remote-k", scope = "weekly", points = 7 }),
        };
        req.Headers.Add("Authorization", "Bearer fed-2");
        var r = await c.SendAsync(req);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var db = Db();
        (await db.LeaderboardScores.FindAsync("remote-k", "weekly"))!.Score.ShouldBe(7);
    }

    [Fact]
    public async Task BadgesEarned_ScopedAndAggregate()
    {
        var c = Dash();
        using var db = Db();
        db.GamiItems.Add(new GamiItem { Kind = "earned", ItemKey = "b1", Actor = "k1", At = "now" });
        db.GamiItems.Add(new GamiItem { Kind = "earned", ItemKey = "b2", Actor = "k2", At = "now" });
        await db.SaveChangesAsync();
        var r = await c.GetAsync("/api/gamification/badges/earned?apiKeyId=k1");
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("badges").GetArrayLength().ShouldBe(1);
        var all = await c.GetAsync("/api/gamification/badges/earned");
        using var d2 = JsonDocument.Parse(await all.Content.ReadAsStringAsync());
        d2.RootElement.GetProperty("badges").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task A2aHistory_UpstreamContract()
    {
        var c = Dash();
        // literal route beats /tasks/{id} — "history" must not 404 as an id
        var bad = await c.GetAsync("/api/a2a/tasks/history?state=bogus");
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await c.PostAsJsonAsync("/api/a2a/tasks",
            new { agent = "a1", skill = "smartRouting", payload = new { model = "m", text = "x" } }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        using var db = Db();
        var t = await db.A2aTasks.FirstAsync();
        t.Skill.ShouldBe("smartRouting");
        t.State = "done";
        t.FinishedAt = "2026-01-01 00:00:00";
        await db.SaveChangesAsync();

        var r = await c.GetAsync("/api/a2a/tasks/history?state=completed&skill=smartRouting");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("items");
        items.GetArrayLength().ShouldBe(1);
        var item = items[0];
        item.GetProperty("state").GetString().ShouldBe("completed"); // ours "done" → upstream
        item.GetProperty("skill").GetString().ShouldBe("smartRouting");
        item.GetProperty("completedAt").GetString().ShouldBe("2026-01-01 00:00:00");

        // wrong upstream state name filters to empty, not error
        var none = await c.GetAsync("/api/a2a/tasks/history?state=working");
        using var d3 = JsonDocument.Parse(await none.Content.ReadAsStringAsync());
        d3.RootElement.GetProperty("items").GetArrayLength().ShouldBe(0);
    }
}
