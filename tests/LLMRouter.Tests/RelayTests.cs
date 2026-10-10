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

/// <summary>SPEC-048: relay tokens — /v1/relay/chat/completions, quota, revoke.</summary>
public class RelayTests : IDisposable
{
    private sealed class CapturingUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public RelayTests()
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

    private async Task SeedAsync()
    {
        await using var db = Db();
        db.EnsureCreated();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c1", Provider = "openai", Name = "main", IsActive = true,
            Data = """{"baseUrl":"http://fake"}""", CreatedAt = "x", UpdatedAt = "x",
        });
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> Chat(HttpClient c, string token, string model = "openai/gpt-4o")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/relay/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model,
                messages = new[] { new { role = "user", content = "hi" } },
                stream = false,
            }),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var r = await c.SendAsync(req);
        var body = await r.Content.ReadAsStringAsync();
        return JsonSerializer.SerializeToElement(new { status = (int)r.StatusCode, body });
    }

    [Fact]
    public async Task RelayToken_dispatches_and_attributes_usage()
    {
        await SeedAsync();
        var token = "rl-test1111111111111111111111111111";
        await using (var db = Db())
        {
            db.RelayTokens.Add(new RelayToken
            { Id = "t1", Token = token, Name = "cli", IsActive = true, CreatedAt = "x" });
            await db.SaveChangesAsync();
        }
        var r = await Chat(_factory.CreateClient(), token);
        r.GetProperty("status").GetInt32().ShouldBe(200, r.GetProperty("body").GetString());
        await using (var db = Db())
        {
            var u = await db.UsageHistory.FirstOrDefaultAsync(u => u.ApiKey == "relay:t1");
            u.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task Revoked_token_returns_401()
    {
        await SeedAsync();
        await using (var db = Db())
        {
            db.RelayTokens.Add(new RelayToken
            { Id = "t2", Token = "rl-dead", IsActive = false, CreatedAt = "x" });
            await db.SaveChangesAsync();
        }
        var r = await Chat(_factory.CreateClient(), "rl-dead");
        r.GetProperty("status").GetInt32().ShouldBe(401);
        // unknown tokens also 401
        var r2 = await Chat(_factory.CreateClient(), "rl-nope");
        r2.GetProperty("status").GetInt32().ShouldBe(401);
    }

    [Fact]
    public async Task Disallowed_model_returns_403()
    {
        await SeedAsync();
        await using (var db = Db())
        {
            db.RelayTokens.Add(new RelayToken
            {
                Id = "t3", Token = "rl-limited", IsActive = true, CreatedAt = "x",
                AllowedModels = """["openai/*"]""",
            });
            await db.SaveChangesAsync();
        }
        var r = await Chat(_factory.CreateClient(), "rl-limited", "anthropic/claude-3");
        r.GetProperty("status").GetInt32().ShouldBe(403);
        var r2 = await Chat(_factory.CreateClient(), "rl-limited", "openai/gpt-4o");
        r2.GetProperty("status").GetInt32().ShouldBe(200, r2.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Daily_request_quota_returns_429()
    {
        await SeedAsync();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        await using (var db = Db())
        {
            db.RelayTokens.Add(new RelayToken
            { Id = "t4", Token = "rl-capped", IsActive = true, QuotaRequests = 2, CreatedAt = "x" });
            db.UsageHistory.Add(new UsageRecord
            { Timestamp = today + " 01:00:00", ApiKey = "relay:t4", Model = "m", Endpoint = "openai" });
            db.UsageHistory.Add(new UsageRecord
            { Timestamp = today + " 02:00:00", ApiKey = "relay:t4", Model = "m", Endpoint = "openai" });
            await db.SaveChangesAsync();
        }
        var r = await Chat(_factory.CreateClient(), "rl-capped");
        r.GetProperty("status").GetInt32().ShouldBe(429);
    }

    [Fact]
    public async Task Crud_create_lists_masked_and_delete_revokes()
    {
        await SeedAsync();
        var c = _factory.CreateClient();
        (await c.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var created = await c.PostAsJsonAsync("/api/relay/tokens",
            new { name = "bot", allowedModels = new[] { "openai/*" } });
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var full = body.GetProperty("token").GetString()!;
        full.ShouldStartWith("rl-");
        var id = body.GetProperty("view").GetProperty("id").GetString()!;

        var list = await c.GetFromJsonAsync<JsonElement>("/api/relay/tokens");
        var row = list.GetProperty("tokens").EnumerateArray().Single(t => t.GetProperty("id").GetString() == id);
        row.GetProperty("token").GetString()!.ShouldContain("…");
        row.GetProperty("token").GetString()!.ShouldNotBe(full);

        (await c.DeleteAsync($"/api/relay/tokens/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var db = Db())
            (await db.RelayTokens.FindAsync(id)).ShouldBeNull();
    }
}
