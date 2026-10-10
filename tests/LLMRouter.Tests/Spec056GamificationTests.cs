using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-056: gamification persistente — eventos, badges, invites.</summary>
public sealed class Spec056GamificationTests : WebAppTestBase
{
    [Fact]
    public async Task EventoDeveIncrementarScore()
    {
        using var db = Db();
        await LLMRouter.Core.Gamification.GamiCore.EventAsync(db, "fx", 10, "teste");
        await LLMRouter.Core.Gamification.GamiCore.EventAsync(db, "fx", 15, "teste2");
        var score = await LLMRouter.Core.Gamification.GamiCore.ScoreAsync(db, "fx");
        Xunit.Assert.Equal(25, score);
    }

    [Fact]
    public async Task DeveConcederBadgeFirstRequest()
    {
        using var db = Db();
        var r = await LLMRouter.Core.Gamification.GamiCore.EventAsync(db, "fx", 1, "req", "request");
        r.GetProperty("awarded").EnumerateArray().Select(x => x.GetString()).ShouldContain("first-request");
        (await db.GamiItems.CountAsync(g => g.Kind == "earned" && g.Actor == "fx")).ShouldBe(1);
    }

    [Fact]
    public async Task EndpointsDevemResponder()
    {
        var client = Client;
        await LoginAsync();
        var post = await client.PostAsJsonAsync("/api/gamification/event", new { actor = "fx", points = 5, reason = "t" });
        post.EnsureSuccessStatusCode();
        var score = await client.GetFromJsonAsync<JsonElement>("/api/gamification/score/fx");
        score.GetProperty("score").GetInt64().ShouldBe(5);
        var inv = await client.PostAsJsonAsync("/api/gamification/invites", new { actor = "fx" });
        var code = (await inv.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        var red = await client.PostAsJsonAsync("/api/gamification/invites/redeem", new { code, actor = "novato" });
        red.EnsureSuccessStatusCode();
        (await client.GetAsync("/api/gamification/leaderboard")).EnsureSuccessStatusCode();
        (await client.GetAsync("/api/gamification/anomalies")).EnsureSuccessStatusCode();
        (await client.GetAsync("/api/gamification/badges/novato")).EnsureSuccessStatusCode();
    }
}
