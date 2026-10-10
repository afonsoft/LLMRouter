using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-036: /api/usage/* analytics endpoints.</summary>
public class UsageAnalyticsEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public UsageAnalyticsEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private async Task LoginAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private void Seed()
    {
        var db = new LlmRouterDbContext(
            new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        // anchor to a fixed hour today so small offsets can never cross a
        // date boundary — a "N minutes ago" seed splits the group near midnight
        var anchor = now.Date.AddHours(Math.Clamp(now.Hour, 1, 23));
        UsageRecord Row(int secsAgo, string provider, string model, string status, long pt, long ct, double cost, long lat, string? conn = null, string? key = null) =>
            new()
            {
                Timestamp = anchor.AddSeconds(-secsAgo).ToString("yyyy-MM-dd HH:mm:ss"),
                Provider = provider, Model = model, Status = status,
                PromptTokens = pt, CompletionTokens = ct, Cost = cost, LatencyMs = lat,
                ConnectionId = conn, ApiKey = key, Endpoint = "chat", Tokens = (pt + ct).ToString(),
            };
        db.UsageHistory.AddRange(
            Row(10, "openai", "gpt-4", "200", 100, 50, 0.01, 500, "c1", "key-aaaaaaaaaaaaaaaa"),
            Row(20, "openai", "gpt-4", "200", 200, 80, 0.02, 800, "c1", "key-aaaaaaaaaaaaaaaa"),
            Row(30, "anthropic", "combo-main", "500", 100, 0, 0, 1500, "c2", "key-bbbbbbbbbbbbbbbb"),
            Row(40, "anthropic", "claude-3", "200", 50, 20, 0.005, 300, "c2"));
        db.Combos.Add(new Combo { Id = "combo-main", Name = "combo-main", Models = "[\"anthropic/claude-3\"]", CreatedAt = now.ToString("o"), UpdatedAt = now.ToString("o") });
        db.SaveChanges();
        db.Dispose();
    }

    private static async Task<JsonElement> Get(HttpClient c, string url)
    {
        var resp = await c.GetAsync(url);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Analytics_summarizes_requests_models_and_providers()
    {
        await LoginAsync(); Seed();
        var r = await Get(_client, "/api/usage/analytics?days=1");
        r.GetProperty("requests").GetInt32().ShouldBe(4);
        r.GetProperty("successRate").GetDouble().ShouldBe(75.0);
        r.GetProperty("promptTokens").GetInt64().ShouldBe(450);
        r.GetProperty("topModels").GetArrayLength().ShouldBeGreaterThan(0);
        r.GetProperty("topProviders").EnumerateArray()
            .ShouldContain(x => x.GetProperty("provider").GetString() == "openai" && x.GetProperty("requests").GetInt64() == 2);
    }

    [Fact]
    public async Task Combo_health_reports_success_latency_and_last_error()
    {
        await LoginAsync(); Seed();
        var r = await Get(_client, "/api/usage/combo-health?days=7");
        var combos = r.GetProperty("combos").EnumerateArray().ToList();
        var c = combos.First(x => x.GetProperty("combo").GetString() == "combo-main");
        c.GetProperty("requests").GetInt64().ShouldBe(1);
        c.GetProperty("successRate").GetDouble().ShouldBe(0);
        c.GetProperty("lastError").ValueKind.ShouldBe(JsonValueKind.Object);
        combos.ShouldContain(x => x.GetProperty("score").GetDouble() >= 0);
    }

    [Fact]
    public async Task Utilization_groups_by_provider_connection_and_key()
    {
        await LoginAsync(); Seed();
        var r = await Get(_client, "/api/usage/utilization?days=7");
        var providers = r.GetProperty("byProvider").EnumerateArray().ToList();
        providers.First(x => x.GetProperty("provider").GetString() == "openai").GetProperty("requests").GetInt64().ShouldBe(2);
        providers.First(x => x.GetProperty("provider").GetString() == "anthropic").GetProperty("errorRate").GetDouble().ShouldBe(50.0);
        r.GetProperty("byConnection").GetArrayLength().ShouldBe(2);
        r.GetProperty("byApiKey").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Model_latency_returns_percentiles()
    {
        await LoginAsync(); Seed();
        var r = await Get(_client, "/api/usage/model-latency?days=7");
        var gpt = r.GetProperty("models").EnumerateArray().First(x => x.GetProperty("model").GetString() == "gpt-4");
        gpt.GetProperty("avgMs").GetInt64().ShouldBe(650);
        gpt.GetProperty("p50Ms").GetInt64().ShouldBe(800);
        gpt.GetProperty("p95Ms").GetInt64().ShouldBe(800);
    }

    [Fact]
    public async Task History_pages_and_filters()
    {
        await LoginAsync(); Seed();
        var r = await Get(_client, "/api/usage/history?provider=openai&limit=10");
        r.GetProperty("total").GetInt32().ShouldBe(2);
        r.GetProperty("rows").GetArrayLength().ShouldBe(2);
        var err = await Get(_client, "/api/usage/history?status=500");
        err.GetProperty("total").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Key_quota_and_pivot_endpoints()
    {
        await LoginAsync(); Seed();
        var kq = await Get(_client, "/api/usage/key-quota");
        kq.GetProperty("keys").GetArrayLength().ShouldBe(2);
        var piv = await Get(_client, "/api/usage/requests-by-provider-date?days=7");
        piv.GetProperty("rows").EnumerateArray()
            .ShouldContain(x => x.GetProperty("provider").GetString() == "openai" && x.GetProperty("requests").GetInt64() == 2);
    }
}
