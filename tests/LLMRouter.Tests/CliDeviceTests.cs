using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-050: CLI device login — connect → approve → poll → whoami.</summary>
public class CliDeviceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public CliDeviceTests()
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

    private HttpClient LoginClient()
    {
        var c = _factory.CreateClient();
        c.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Result
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return c;
    }

    [Fact]
    public async Task Full_flow_connect_approve_poll_whoami()
    {
        await using (var db = Db()) db.EnsureCreated();
        var cli = _factory.CreateClient(); // unauthenticated CLI

        // 1. connect → pending token + url
        var conn = await cli.PostAsJsonAsync("/api/cli/connect", new { deviceName = "codex-mac" });
        conn.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cbody = await conn.Content.ReadFromJsonAsync<JsonElement>();
        var token = cbody.GetProperty("token").GetString()!;
        token.Length.ShouldBe(12);
        cbody.GetProperty("url").GetString().ShouldBe($"/connect/{token}");

        // 2. CLI polls → 202 pending
        (await cli.GetAsync($"/api/cli/tokens/{token}")).StatusCode
            .ShouldBe(HttpStatusCode.Accepted);

        // 3. owner approves (dashboard auth)
        var dash = LoginClient();
        var appr = await dash.PostAsJsonAsync($"/api/cli/tokens/{token}/approve", new { });
        appr.StatusCode.ShouldBe(HttpStatusCode.OK);
        var apiKey = (await appr.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("apiKey").GetString()!;
        apiKey.ShouldStartWith("sk-cli-");

        // 4. CLI polls → approved + apiKey + baseUrl
        var poll = await cli.GetFromJsonAsync<JsonElement>($"/api/cli/tokens/{token}");
        poll.GetProperty("state").GetString().ShouldBe("approved");
        poll.GetProperty("apiKey").GetString().ShouldBe(apiKey);
        poll.GetProperty("baseUrl").GetString()!.ShouldStartWith("http");

        // 5. whoami with the minted key
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/cli/whoami");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        var who = await (await cli.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        who.GetProperty("authenticated").GetBoolean().ShouldBe(true);
        who.GetProperty("name").GetString().ShouldBe("cli:codex-mac");
    }

    [Fact]
    public async Task Expired_token_returns_410()
    {
        await using (var db = Db())
        {
            db.EnsureCreated();
            db.CliTokens.Add(new CliToken
            {
                Id = "x1", Token = "OLDTOKEN1234", State = "pending",
                CreatedAt = DateTime.UtcNow.AddMinutes(-30).ToString("yyyy-MM-dd HH:mm:ss"),
            });
            await db.SaveChangesAsync();
        }
        var cli = _factory.CreateClient();
        (await cli.GetAsync("/api/cli/tokens/OLDTOKEN1234")).StatusCode
            .ShouldBe(HttpStatusCode.Gone);
        // approve of an expired token also fails
        var dash = LoginClient();
        (await dash.PostAsJsonAsync("/api/cli/tokens/OLDTOKEN1234/approve", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Revoked_token_returns_410_and_key_disabled()
    {
        await using (var db = Db()) db.EnsureCreated();
        var dash = LoginClient();
        var cli = _factory.CreateClient();
        var conn = await cli.PostAsJsonAsync("/api/cli/connect", new { });
        var token = (await conn.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;
        var appr = await dash.PostAsJsonAsync($"/api/cli/tokens/{token}/approve", new { });
        var apiKey = (await appr.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("apiKey").GetString()!;

        (await dash.PostAsJsonAsync($"/api/cli/tokens/{token}/revoke", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cli.GetAsync($"/api/cli/tokens/{token}")).StatusCode
            .ShouldBe(HttpStatusCode.Gone);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/cli/whoami");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        (await cli.SendAsync(req)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Approve_requires_dashboard_auth()
    {
        await using (var db = Db()) db.EnsureCreated();
        var cli = _factory.CreateClient();
        var conn = await cli.PostAsJsonAsync("/api/cli/connect", new { });
        var token = (await conn.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;
        (await cli.PostAsJsonAsync($"/api/cli/tokens/{token}/approve", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
