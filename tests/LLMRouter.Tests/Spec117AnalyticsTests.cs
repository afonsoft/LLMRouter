using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-117: analytics parity — /api/usage/utilization?range= (upstream
/// quota_snapshots contract) + /api/usage/combo-health autopilot report.</summary>
public class Spec117AnalyticsTests : WebAppTestBase
{
    private static string Now(int back = 0) =>
        DateTime.UtcNow.AddMinutes(-back).ToString("yyyy-MM-dd HH:mm:ss");

    private async Task SeedSnap(string provider, string? conn, double? pct, int exhausted = 0, int back = 0, string window = "5h")
    {
        using var db = Db();
        db.QuotaSnapshots.Add(new QuotaSnapshot
        {
            Provider = provider, ConnectionId = conn, WindowKey = window,
            RemainingPercentage = pct, IsExhausted = exhausted, CreatedAt = Now(back),
        });
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> GetUtil(string qs)
    {
        var r = await Client.GetAsync($"api/usage/utilization?{qs}");
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
    }

    [Fact]
    public async Task Utilization_range_retorna_buckets_agregados()
    {
        await LoginAsync();
        for (var i = 0; i < 6; i++) await SeedSnap("openai", "c1", 90 - i * 10, back: i * 5);
        await SeedSnap("anthropic", "c2", 40);

        var el = await GetUtil("range=24h");
        el.GetProperty("timeRange").GetString().ShouldBe("24h");
        el.GetProperty("bucketSizeMinutes").GetInt32().ShouldBe(10);
        var providers = el.GetProperty("providers").EnumerateArray().Select(p => p.GetString()).ToList();
        providers.ShouldContain("openai");
        providers.ShouldContain("anthropic");
        var data = el.GetProperty("data").EnumerateArray().ToList();
        data.Count.ShouldBeGreaterThan(2);
        data.ShouldAllBe(d => d.GetProperty("remainingPct").ValueKind == JsonValueKind.Number);
    }

    [Fact]
    public async Task Utilization_aggregateBy_connection_chaveia_e_meta()
    {
        await LoginAsync();
        using (var db = Db())
        {
            db.ProviderConnections.Add(new ProviderConnection
            {
                Id = "conn-abc", Provider = "openai", AuthType = "apikey", Name = "Work acct",
                Email = "w@x.dev", IsActive = true, Priority = 0,
            });
            await db.SaveChangesAsync();
        }
        await SeedSnap("openai", "conn-abc", 75);

        var el = await GetUtil("range=24h&aggregateBy=connection");
        var data = el.GetProperty("data").EnumerateArray().ToList();
        data.ShouldContain(d => d.GetProperty("provider").GetString() == "openai:conn-abc");
        var meta = el.GetProperty("connectionMeta").GetProperty("conn-abc");
        meta.GetProperty("displayName").GetString().ShouldBe("Work acct");
        meta.GetProperty("email").GetString().ShouldBe("w@x.dev");
    }

    [Fact]
    public async Task Utilization_range_invalido_400()
    {
        await LoginAsync();
        var r = await Client.GetAsync("api/usage/utilization?range=90d");
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Utilization_sem_dados_retorna_vazio()
    {
        await LoginAsync();
        var el = await GetUtil("range=1h");
        el.GetProperty("providers").GetArrayLength().ShouldBe(0);
        el.GetProperty("data").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task QuotaSnapshots_dedup_identicos_muda_insere()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        var w = new QuotaSnapshots.Window(80, null, null, null);
        await QuotaSnapshots.SaveAsync(db, "openai", "c1", new Dictionary<string, QuotaSnapshots.Window> { ["5h"] = w });
        await QuotaSnapshots.SaveAsync(db, "openai", "c1", new Dictionary<string, QuotaSnapshots.Window> { ["5h"] = w });
        db.QuotaSnapshots.Count().ShouldBe(1);
        var w2 = new QuotaSnapshots.Window(70, null, null, null);
        await QuotaSnapshots.SaveAsync(db, "openai", "c1", new Dictionary<string, QuotaSnapshots.Window> { ["5h"] = w2 });
        db.QuotaSnapshots.Count().ShouldBe(2);
    }

    [Fact]
    public async Task Combo_health_autopilot_agrega_status_e_issues()
    {
        await LoginAsync();
        string comboId;
        using (var db = Db())
        {
            var combo = new Combo { Id = "combo-1", Name = "mycombo", Kind = "round-robin", Models = "[\"openai/gpt-4\"]" };
            db.Combos.Add(combo);
            db.UsageHistory.Add(new UsageRecord
            {
                Timestamp = Now(), Provider = "openai", Model = "mycombo", ConnectionId = "c1",
                ApiKey = "k1", Endpoint = "/v1/chat/completions", PromptTokens = 1, CompletionTokens = 1,
                Cost = 0.001, LatencyMs = 50, Status = "200", Meta = "{}",
            });
            for (var i = 0; i < 11; i++)
                db.UsageHistory.Add(new UsageRecord
                {
                    Timestamp = Now(), Provider = "openai", Model = "mycombo", ConnectionId = "c1",
                    ApiKey = "k1", Endpoint = "/v1/chat/completions", PromptTokens = 1, CompletionTokens = 1,
                    Cost = 0.001, LatencyMs = 50, Status = "500", Meta = "{}",
                });
            await db.SaveChangesAsync();
            comboId = combo.Id;
        }

        var r = await Client.GetAsync("api/usage/combo-health?days=7");
        r.EnsureSuccessStatusCode();
        var el = (await r.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;

        var ap = el.GetProperty("autopilot");
        var sum = ap.GetProperty("summary");
        sum.GetProperty("issueCount").GetInt32().ShouldBeGreaterThan(0);
        var combos = ap.GetProperty("combos").EnumerateArray().ToList();
        combos.ShouldContain(c => c.GetProperty("comboName").GetString() == "mycombo");
        var mine = combos.First(c => c.GetProperty("comboName").GetString() == "mycombo");
        mine.GetProperty("issues").EnumerateArray()
            .ShouldContain(i => i.GetProperty("kind").GetString() == "combo_low_success_rate"
                && i.GetProperty("severity").GetString() == "critical"
                && i.GetProperty("actions").EnumerateArray()
                    .Any(a => a.GetProperty("href").GetString() == $"/dashboard/combos/{comboId}"));
    }

    [Fact]
    public async Task Combo_health_sem_issues_status_healthy()
    {
        await LoginAsync();
        var r = await Client.GetAsync("api/usage/combo-health?days=7");
        r.EnsureSuccessStatusCode();
        var el = (await r.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
        var ap = el.GetProperty("autopilot");
        ap.GetProperty("status").GetString().ShouldBe("healthy");
        ap.GetProperty("summary").GetProperty("issueCount").GetInt32().ShouldBe(0);
    }
}
