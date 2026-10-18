using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Net;
using LLMRouter.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace LLMRouter.Tests;

/// <summary>SPEC-144: plan tiers — public /models tiers block, PATCH user plan, free-tier daily cap.</summary>
public class Spec144PlanTiersTests : WebAppTestBase
{
    [Fact]
    public async Task PublicModels_returns_tiers_block()
    {
        var r = await Client.GetAsync("/api/public/models");
        r.EnsureSuccessStatusCode();
        var j = await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        j.TryGetProperty("platformFeePct", out _).ShouldBeTrue();
        var tiers = j.GetProperty("tiers").EnumerateArray().ToList();
        tiers.Select(t => t.GetProperty("id").GetString()).ShouldBe(["free", "standard"]);
        tiers[0].GetProperty("dailyLimit").GetInt32().ShouldBe(50);
        tiers[1].GetProperty("dailyLimit").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Admin_patch_sets_plan()
    {
        await LoginAsync();
        var created = await Client.PostAsJsonAsync("/api/users", new { email = "p@x.com", name = "P" });
        var u = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var id = u.GetProperty("id").GetString();
        u.GetProperty("plan").GetString().ShouldBe("free");

        var patch = await Client.PatchAsJsonAsync($"/api/users/{id}", new { plan = "standard" });
        patch.EnsureSuccessStatusCode();
        (await patch.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("plan").GetString().ShouldBe("standard");

        // invalid plan is ignored
        var bad = await Client.PatchAsJsonAsync($"/api/users/{id}", new { plan = "gold" });
        (await bad.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("plan").GetString().ShouldBe("standard");
    }

    [Fact]
    public async Task Free_plan_user_hits_daily_cap()
    {
        var db = Db();
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is null) { settings = new SettingRow { Id = 1, Data = "{}" }; db.Settings.Add(settings); }
        settings.Data = """{"payments":{"freeDailyLimit":1}}""";

        var user = new User { Id = "u-free", Email = "f@x.com", Name = "F", CreatedAt = DateTime.UtcNow.ToString("O"), Plan = "free" };
        db.Users.Add(user);
        var vk = new UserKey
        {
            Id = "k1", UserId = user.Id, Key = "vk-free1", Name = "t", CreatedAt = DateTime.UtcNow.ToString("O"),
            ModelsAllow = "[]", BudgetUsd = 0, BudgetPeriod = "none", IsActive = true, SpendUsd = 0, SpendPeriodStart = "",
        };
        db.UserKeys.Add(vk);
        // already used 1 request today = at the cap of 1
        db.UsageHistory.Add(new UsageRecord
        {
            Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"), Provider = "p", Model = "m",
            ApiKey = "vk-free1", Endpoint = "chat", Status = "200", Tokens = "1", Meta = "{}",
            UserId = user.Id, UserKeyId = vk.Id,
        });
        await db.SaveChangesAsync();

        var api = Factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new("Bearer", "vk-free1");
        var r = await api.PostAsJsonAsync("/v1/chat/completions", new { model = "p/m", messages = new[] { new { role = "user", content = "hi" } } });
        r.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("free_tier_limit");
    }
}
