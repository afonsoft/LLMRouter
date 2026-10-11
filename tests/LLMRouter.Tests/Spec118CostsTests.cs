using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-118: costs parity — /api/usage/analytics?range= upstream contract,
/// pricing sync/clear-synced + synced flags, catalogUserPricing precedence.</summary>
public class Spec118CostsTests : WebAppTestBase
{
    private static string Now(int daysBack = 0) =>
        DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-dd HH:mm:ss");

    private async Task SeedUsage(string provider, string model, double cost, int daysBack = 0,
        string? key = "k1", string? conn = "c1", string? meta = null)
    {
        using var db = Db();
        db.UsageHistory.Add(new UsageRecord
        {
            Timestamp = Now(daysBack), Provider = provider, Model = model, ConnectionId = conn,
            ApiKey = key, Endpoint = "/v1/chat/completions", PromptTokens = 10, CompletionTokens = 20,
            Cost = cost, LatencyMs = 50, Status = "200", Meta = meta,
        });
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> GetJson(string url)
    {
        var r = await Client.GetAsync(url);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
    }

    [Fact]
    public async Task Analytics_range_agrega_summary_e_dimensoes()
    {
        await LoginAsync();
        await SeedUsage("openai", "gpt-4", 0.01, key: "k1", conn: "c1");
        await SeedUsage("openai", "gpt-3.5", 0.02, key: "k2", conn: "c2", meta: """{"serviceTier":"priority"}""");
        await SeedUsage("groq", "llama", 0.005, meta: """{"fallback":true}""");

        var el = await GetJson("api/usage/analytics?range=30d&presets=1d,7d,30d");
        var s = el.GetProperty("summary");
        s.GetProperty("totalRequests").GetInt32().ShouldBe(3);
        s.GetProperty("totalCost").GetDouble().ShouldBe(0.035, 0.0001);
        s.GetProperty("uniqueModels").GetInt32().ShouldBe(3);
        s.GetProperty("uniqueAccounts").GetInt32().ShouldBe(2);
        s.GetProperty("uniqueApiKeys").GetInt32().ShouldBe(2);
        s.GetProperty("fallbackCount").GetInt32().ShouldBe(1);
        s.GetProperty("streak").GetInt32().ShouldBeGreaterThanOrEqualTo(1);

        var bp = el.GetProperty("byProvider").EnumerateArray().ToList();
        bp.Count.ShouldBe(2);
        bp[0].GetProperty("provider").GetString().ShouldBe("openai");
        bp[0].GetProperty("cost").GetDouble().ShouldBe(0.03, 0.0001);
        el.GetProperty("byModel").GetArrayLength().ShouldBe(3);
        el.GetProperty("byApiKey").GetArrayLength().ShouldBe(2);
        el.GetProperty("byAccount").GetArrayLength().ShouldBe(2);

        var tiers = el.GetProperty("byServiceTier").EnumerateArray().ToList();
        tiers.ShouldContain(t => t.GetProperty("serviceTier").GetString() == "priority");
        el.GetProperty("dailyTrend").GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);
        el.GetProperty("presetSummaries").GetProperty("1d").GetProperty("totalCost").GetDouble()
            .ShouldBe(0.035, 0.0001);
    }

    [Fact]
    public async Task Analytics_range_all_inclui_antigo()
    {
        await LoginAsync();
        await SeedUsage("openai", "gpt-4", 0.01, daysBack: 400);
        var el = await GetJson("api/usage/analytics?range=all");
        el.GetProperty("summary").GetProperty("totalRequests").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Analytics_range_invalido_400()
    {
        await LoginAsync();
        var r = await Client.GetAsync("api/usage/analytics?range=2x");
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Pricing_sync_fixture_marca_synced_e_clear_limpa()
    {
        await LoginAsync();
        var any = (await GetJson("api/pricing")).GetProperty("catalog").EnumerateArray().First();
        var pm = $"{any.GetProperty("provider").GetString()}/{any.GetProperty("model").GetString()}";
        var synced = await Client.PostAsJsonAsync("api/pricing/sync",
            new { items = new[] { new { model = pm, inputPer1M = 2.5, outputPer1M = 10.0 } } });
        synced.EnsureSuccessStatusCode();
        (await synced.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement
            .GetProperty("synced").GetInt32().ShouldBe(1);

        var cat = (await GetJson("api/pricing")).GetProperty("catalog").EnumerateArray()
            .First(x => $"{x.GetProperty("provider").GetString()}/{x.GetProperty("model").GetString()}" == pm);
        cat.GetProperty("synced").GetBoolean().ShouldBeTrue();

        var cleared = await Client.PostAsJsonAsync("api/pricing/clear-synced", new { });
        cleared.EnsureSuccessStatusCode();
        var cat2 = (await GetJson("api/pricing")).GetProperty("catalog").EnumerateArray()
            .First(x => $"{x.GetProperty("provider").GetString()}/{x.GetProperty("model").GetString()}" == pm);
        cat2.GetProperty("synced").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Pricing_override_precede_synced()
    {
        await LoginAsync();
        var any = (await GetJson("api/pricing")).GetProperty("catalog").EnumerateArray().First();
        var prov = any.GetProperty("provider").GetString();
        var mod = any.GetProperty("model").GetString();
        await Client.PutAsJsonAsync($"api/pricing/{prov}/{mod}", new { input = 1.0, output = 2.0 });
        var cat = (await GetJson("api/pricing")).GetProperty("catalog").EnumerateArray()
            .First(x => x.GetProperty("provider").GetString() == prov
                && x.GetProperty("model").GetString() == mod);
        cat.GetProperty("hasOverride").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Analytics_legacy_days_shape_preservado()
    {
        await LoginAsync();
        var el = await GetJson("api/usage/analytics?days=7");
        el.TryGetProperty("topModels", out _).ShouldBeTrue();
        el.TryGetProperty("daily", out _).ShouldBeTrue();
    }
}
