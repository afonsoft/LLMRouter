using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-054 remainder: health-matrix/autopilot, deprecated/expiration,
/// free-onboarding, openrouter-stats, web-session, per-conn rules.</summary>
public class ProviderOpsSpec54Tests : IDisposable
{
    private sealed class StubUpstream : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string Body = """{"data":[{"id":"m1"},{"id":"m2"}]}""";
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
        public string? LastBody;
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly StubUpstream _up = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ProviderOpsSpec54Tests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                    c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
                .ConfigureServices(s =>
                    s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => _up)));
    }

    public void Dispose()
    {
        _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db() => new(new DbContextOptionsBuilder<LlmRouterDbContext>()
        .UseSqlite($"Data Source={_dbPath}").Options);

    private HttpClient LoginClient()
    {
        var c = _factory.CreateClient();
        c.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Result
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return c;
    }

    private async Task<string> SeedConnAsync(string data, bool active = true)
    {
        await using var db = Db();
        db.EnsureCreated();
        var c = new ProviderConnection
        {
            Id = Guid.NewGuid().ToString("N")[..8], Provider = "openai", AuthType = "apikey",
            Name = "t", IsActive = active, Data = data, CreatedAt = "x", UpdatedAt = "x",
        };
        db.ProviderConnections.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    private async Task SeedUsageAsync(string connId, int ok, int bad)
    {
        await using var db = Db();
        for (var i = 0; i < ok + bad; i++)
            db.UsageHistory.Add(new UsageRecord
            {
                ConnectionId = connId, Provider = "openai",
                Model = "m", Endpoint = "chat", Status = i < ok ? "200" : "500",
                Timestamp = DateTime.UtcNow.AddSeconds(-i).ToString("o"), LatencyMs = 10,
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Health_matrix_returns_checks_per_connection()
    {
        var id = await SeedConnAsync("""{"baseUrl":"http://x","apiKey":"k"}""");
        await SeedUsageAsync(id, ok: 3, bad: 1);
        var c = LoginClient();
        var r = await c.GetFromJsonAsync<JsonElement>("/api/providers/health-matrix?n=5");
        var row = r.GetProperty("connections").EnumerateArray().First(x => x.GetProperty("id").GetString() == id);
        row.GetProperty("checks").GetArrayLength().ShouldBe(4);
        row.GetProperty("healthy").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Health_autopilot_sweep_disables_failing_connection()
    {
        var id = await SeedConnAsync("""{"baseUrl":"http://x","apiKey":"k"}""");
        await SeedUsageAsync(id, ok: 0, bad: 6);
        var c = LoginClient();
        (await c.PutAsJsonAsync("/api/providers/health-autopilot",
            new { enabled = true, failureThreshold = 0.5, minChecks = 5 }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var r = await c.PostAsJsonAsync("/api/providers/health-autopilot/actions", new { action = "sweep" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("disabled").EnumerateArray().Select(x => x.GetString()).ShouldContain(id);
        await using var db = Db();
        (await db.ProviderConnections.FindAsync(id))!.IsActive.ShouldBeFalse();
        // retest re-enables when the upstream now answers 200
        var rr = await c.PostAsJsonAsync("/api/providers/health-autopilot/actions", new { action = "retest" });
        (await rr.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("reenabled").EnumerateArray().Select(x => x.GetString()).ShouldContain(id);
    }

    [Fact]
    public async Task Param_filters_strip_fields_before_upstream()
    {
        var id = await SeedConnAsync(
            """{"baseUrl":"http://f","apiKey":"k","paramFilters":["top_p","seed"]}""");
        var c = LoginClient();
        var r = await c.PostAsJsonAsync("/v1/embeddings",
            new { model = "text-embedding-3-small", input = "x", top_p = 0.5, seed = 42 });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = _up.LastBody!;
        body.ShouldContain("input");
        body.ShouldNotContain("top_p");
        body.ShouldNotContain("seed");
    }

    [Fact]
    public async Task Interception_rules_rewrite_model()
    {
        var id = await SeedConnAsync(
            """{"baseUrl":"http://i","apiKey":"k","interceptionRules":[{"match":{"model":"*"},"rewrite":{"model":"forced-model","set":{"stream":false},"drop":["temperature"]}}]}""");
        var c = LoginClient();
        var r = await c.PostAsJsonAsync("/v1/embeddings",
            new { model = "anything", input = "x", temperature = 0.7 });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = _up.LastBody!;
        body.ShouldContain("forced-model");
        body.ShouldNotContain("temperature");
    }

    [Fact]
    public async Task Cc_alias_maps_model_name()
    {
        var id = await SeedConnAsync(
            """{"baseUrl":"http://a","apiKey":"k","ccAlias":{"sonnet":"claude-sonnet-4-5"}}""");
        var c = LoginClient();
        var r = await c.PostAsJsonAsync("/v1/embeddings", new { model = "sonnet", input = "x" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        _up.LastBody!.ShouldContain("claude-sonnet-4-5");
    }

    [Fact]
    public async Task Sync_models_fetches_and_stores_model_list()
    {
        var id = await SeedConnAsync("""{"baseUrl":"http://sync","apiKey":"k"}""");
        var c = LoginClient();
        var r = await c.PostAsJsonAsync($"/api/provider-connections/{id}/sync-models", new { });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("count").GetInt32().ShouldBe(2);
        _up.Last!.RequestUri!.ToString().ShouldBe("http://sync/models");
        await using var db = Db();
        (await db.ProviderConnections.FindAsync(id))!.Data.ShouldContain("syncedModels");
    }

    [Fact]
    public async Task Deprecated_and_expiration_list_flagged_connections()
    {
        var d1 = await SeedConnAsync("""{"baseUrl":"http://x","apiKey":"k","deprecated":true}""");
        var e1 = await SeedConnAsync(
            $$"""{"baseUrl":"http://x","apiKey":"k","expiresAt":"{{DateTime.UtcNow.AddDays(10):o}}"}""");
        var c = LoginClient();
        var dep = await c.GetFromJsonAsync<JsonElement>("/api/providers/deprecated");
        dep.GetProperty("connections").EnumerateArray().Select(x => x.GetProperty("id").GetString())
            .ShouldContain(d1);
        var exp = await c.GetFromJsonAsync<JsonElement>("/api/providers/expiration");
        exp.GetProperty("expiringSoon").EnumerateArray().Select(x => x.GetProperty("id").GetString())
            .ShouldContain(e1);
    }

    [Fact]
    public async Task Bulk_web_session_validates_contract_fields()
    {
        var c = LoginClient();
        (await c.PutAsJsonAsync("/api/providers/web-session-contract",
            new { requiredFields = new[] { "sessionToken" } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var r = await c.PostAsJsonAsync("/api/providers/bulk-web-session", new
        {
            provider = "chatgpt-web",
            sessions = new object[]
            {
                new { name = "s1", sessionToken = "tok-1" },
                new { name = "s2" },
            },
        });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("created").GetInt32().ShouldBe(1);
        var results = body.GetProperty("results").EnumerateArray().ToList();
        results[0].GetProperty("ok").GetBoolean().ShouldBeTrue();
        results[1].GetProperty("ok").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Codex_doctor_reports_session_checks()
    {
        var id = await SeedConnAsync(
            """{"baseUrl":"http://x","session":{"sessionToken":"t","expiresAt":"2999-01-01T00:00:00Z"}}""");
        var c = LoginClient();
        var r = await c.PostAsJsonAsync($"/api/provider-connections/{id}/chatgpt-web-codex-doctor", new { });
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("ok").GetBoolean().ShouldBeTrue();
        body.GetProperty("checks").GetArrayLength().ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Openrouter_stats_aggregates_usage()
    {
        var id = await SeedConnAsync("""{"baseUrl":"http://or","apiKey":"k"}""");
        await using (var db0 = Db())
        {
            var conn = await db0.ProviderConnections.FindAsync(id);
            conn!.Provider = "openrouter-main";
            await db0.SaveChangesAsync();
        }
        await SeedUsageAsync(id, ok: 4, bad: 1);
        var c = LoginClient();
        var r = await c.GetFromJsonAsync<JsonElement>("/api/providers/openrouter-stats");
        var row = r.GetProperty("connections").EnumerateArray().First(x => x.GetProperty("id").GetString() == id);
        row.GetProperty("requests").GetInt32().ShouldBe(5);
        row.GetProperty("errors").GetInt32().ShouldBe(1);
    }
}
