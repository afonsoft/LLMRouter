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

namespace LLMRouter.Tests;

/// <summary>SPEC-047: ip-filter, payload rules, reasoning simulate,
/// free proxies, task routing.</summary>
public class SettingsRoutingTests : IDisposable
{
    private sealed class CapturingUpstream : HttpMessageHandler
    {
        public static string? LastBody;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            LastBody = r.Content?.ReadAsStringAsync(ct).Result;
            var body = """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public SettingsRoutingTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                    c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
                .ConfigureServices(s =>
                    s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => new CapturingUpstream())));
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

    private HttpClient GatewayClient()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("x-api-key", "sk-rl-route-test");
        return c;
    }

    private async Task SeedAsync(string settingsJson)
    {
        await using var db = Db();
        db.EnsureCreated();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c1", Provider = "openai", Name = "main", IsActive = true,
            Data = """{"baseUrl":"http://fake"}""", CreatedAt = "x", UpdatedAt = "x",
        });
        db.ApiKeys.Add(new ApiKey { Id = "k1", Key = "sk-rl-route-test", Name = "t", IsActive = true, CreatedAt = "x" });
        if (await db.Settings.FirstOrDefaultAsync() is { } s) s.Data = settingsJson;
        else db.Settings.Add(new SettingRow { Data = settingsJson });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task IpFilter_deny_blocks_remote_ip()
    {
        await SeedAsync("""{"ipFilter":{"mode":"deny","cidrs":["10.9.8.0/24"]}}""");
        // TestServer.SendAsync lets us set the connection's remote ip directly
        using var resp = new MemoryStream();
        var rc = await _factory.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Path = "/api/jobs"; // authenticated endpoint — 401 if filter lets it through
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.9.8.7");
            ctx.Response.Body = resp;
        });
        rc.Response.StatusCode.ShouldBe(403,
            $"body={Encoding.UTF8.GetString(resp.ToArray())}");
    }

    [Fact]
    public async Task IpFilter_allow_passes_non_matching_cidr()
    {
        await SeedAsync("""{"ipFilter":{"mode":"allow","cidrs":["10.9.8.0/24"]}}""");
        using var resp = new MemoryStream();
        var rc = await _factory.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Path = "/api/jobs";
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
            ctx.Response.Body = resp;
        });
        rc.Response.StatusCode.ShouldBe(403,
            $"body={Encoding.UTF8.GetString(resp.ToArray())}");
    }

    [Fact]
    public async Task PayloadRule_redacts_field_before_upstream()
    {
        await SeedAsync("""{"payloadRules":[{"action":"redact","field":"metadata.token"}]}""");
        var r = await GatewayClient().PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "openai/gpt-4o",
            messages = new[] { new { role = "user", content = "hi" } },
            metadata = new { token = "SECRET-VALUE" },
            stream = false,
        });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        CapturingUpstream.LastBody.ShouldNotContain("SECRET-VALUE");
        CapturingUpstream.LastBody.ShouldContain("\"token\":\"***\"");
    }

    [Fact]
    public async Task Reasoning_simulate_returns_matched_rule()
    {
        await SeedAsync("""{"reasoningRoutingRules":[{"pattern":"o1|deepseek","effort":"budget","budgetTokens":2048}]}""");
        var c = LoginClient();
        var r = await c.PostAsJsonAsync("/api/settings/reasoning-rules/simulate",
            new { model = "openai/o1-preview" });
        var b = await r.Content.ReadFromJsonAsync<JsonElement>();
        b.GetProperty("matched").GetString().ShouldBe("o1|deepseek");
        b.GetProperty("effort").GetString().ShouldBe("budget");
        b.GetProperty("body").GetProperty("thinking")
            .GetProperty("budget_tokens").GetInt32().ShouldBe(2048);
    }

    [Fact]
    public async Task FreeProxy_entries_populate_pool()
    {
        await SeedAsync("{}");
        var c = LoginClient();
        // 127.0.0.1:1 refuses instantly — keeps the health check fast
        var r = await c.PostAsJsonAsync("/api/proxy-pools/fetch-free",
            new { entries = new[] { "127.0.0.1:1" } });
        var b = await r.Content.ReadFromJsonAsync<JsonElement>();
        b.GetProperty("count").GetInt32().ShouldBe(1);
        await using (var db = Db())
        {
            var pool = await db.ProxyPools.FindAsync("free-proxies");
            pool.ShouldNotBeNull();
            pool!.Data.ShouldContain("127.0.0.1:1");
        }
    }

    [Fact]
    public async Task TaskRouting_resolve_returns_mapped_model()
    {
        await SeedAsync("""{"taskRouting":{"code-review":"openai/gpt-4o"}}""");
        var c = LoginClient();
        var r = await c.PostAsJsonAsync("/api/settings/task-routing/resolve",
            new { taskType = "code-review" });
        var b = await r.Content.ReadFromJsonAsync<JsonElement>();
        b.GetProperty("model").GetString().ShouldBe("openai/gpt-4o");
    }
}
