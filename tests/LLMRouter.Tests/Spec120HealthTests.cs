using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-120: health + leaderboard parity — db migrations diagnostics,
/// admin-only auto-repair, status fields, leaderboard window/by params + stats.</summary>
public class Spec120HealthTests : WebAppTestBase
{
    private async Task<JsonElement> GetJson(string url)
    {
        var r = await Client.GetAsync(url);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
    }

    [Fact]
    public async Task Status_retorna_version_uptime_memory_providers()
    {
        await LoginAsync();
        var el = await GetJson("api/omniroute/status");
        el.TryGetProperty("version", out _).ShouldBeTrue();
        el.TryGetProperty("uptimeSec", out _).ShouldBeTrue();
        el.TryGetProperty("memoryRss", out _).ShouldBeTrue();
        el.TryGetProperty("providers", out _).ShouldBeTrue();
        el.TryGetProperty("providersTotal", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Db_migrations_reporta_applied_available_pending()
    {
        await LoginAsync();
        var el = await GetJson("api/db/migrations");
        el.GetProperty("applied").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        el.GetProperty("available").GetInt32().ShouldBeGreaterThanOrEqualTo(el.GetProperty("applied").GetInt32());
        el.GetProperty("pending").GetArrayLength().ShouldBe(0);
        el.GetProperty("ok").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Autorepair_admin_only_e_audita()
    {
        await LoginAsync();
        // dashboard-cookie client works
        var ok = await Client.PostAsJsonAsync("api/db/autorepair", new { });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await ok.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
        body.GetProperty("ok").GetBoolean().ShouldBeTrue();

        // api-key-authed (non-admin) client → 403
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LLMRouter.Core.Data.LlmRouterDbContext>();
        db.ApiKeys.Add(new ApiKey { Id = "k-lb", Key = "test-lb-key", IsActive = true, Name = "t" });
        await db.SaveChangesAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "api/db/autorepair")
        {
            Content = JsonContent.Create(new { }),
        };
        req.Headers.Add("x-api-key", "test-lb-key");
        var anon = Factory.CreateClient();
        var denied = await anon.SendAsync(req);
        denied.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Leaderboard_window_e_by_e_stats()
    {
        await LoginAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LLMRouter.Core.Data.LlmRouterDbContext>();
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        db.UsageHistory.Add(new UsageRecord
        {
            Timestamp = now, Provider = "openai", Model = "gpt-4", ApiKey = "k",
            PromptTokens = 10, CompletionTokens = 20, Cost = 0.01, LatencyMs = 1, Status = "200",
        });
        db.UsageHistory.Add(new UsageRecord
        {
            Timestamp = now, Provider = "groq", Model = "llama", ApiKey = "k",
            PromptTokens = 5, CompletionTokens = 5, Cost = 0.005, LatencyMs = 1, Status = "200",
        });
        await db.SaveChangesAsync();

        var all = await GetJson("api/leaderboard?window=all&by=requests");
        all.GetProperty("stats").GetProperty("totalRequests").GetInt64().ShouldBeGreaterThanOrEqualTo(2);
        all.GetProperty("stats").TryGetProperty("requestsToday", out _).ShouldBeTrue();
        all.GetProperty("stats").TryGetProperty("tokensToday", out _).ShouldBeTrue();
        all.GetProperty("window").GetString().ShouldBe("all");

        var weekly = await GetJson("api/leaderboard?window=weekly&by=tokens");
        weekly.GetProperty("window").GetString().ShouldBe("weekly");
        weekly.GetProperty("by").GetString().ShouldBe("tokens");
        var lb = weekly.GetProperty("leaderboard").EnumerateArray().ToList();
        lb.ShouldNotBeEmpty();
        // by=tokens ranks by token volume desc
        var tokens = lb.Select(x => x.GetProperty("tokens").GetInt64()).ToList();
        tokens.ShouldBe(tokens.OrderByDescending(x => x).ToList());
    }

    [Fact]
    public async Task Health_degradation_e_quota_shapes()
    {
        await LoginAsync();
        var dg = await GetJson("api/health/degradation");
        dg.TryGetProperty("degraded", out _).ShouldBeTrue();
        var q = await GetJson("api/quota");
        q.TryGetProperty("quota", out var qa).ShouldBeTrue();
        if (qa.GetArrayLength() > 0)
        {
            qa[0].TryGetProperty("daily", out _).ShouldBeTrue();
            qa[0].TryGetProperty("monthly", out _).ShouldBeTrue();
        }
    }
}
