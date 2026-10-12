using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>
/// SPEC-130: provider auth apply-local/export + refresh — codex-auth/claude-auth
/// file builders, attachment downloads, writes-disabled guard (403 writes_disabled),
/// codex skipped_present_fresh decision, claude merge preserving mcpOAuth, and
/// the generic /refresh 404/502 contract.
/// </summary>
public class Spec130ProviderAuthTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec130ProviderAuthTests()
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

    private async Task<HttpClient> Dash()
    {
        var c = _factory.CreateClient();
        var r = await c.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        r.EnsureSuccessStatusCode();
        return c;
    }

    private async Task<string> SeedConn(string provider, JsonObject data)
    {
        _ = await Dash(); // boot app -> migrations
        await using var db = Db();
        var conn = new ProviderConnection
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Provider = provider, Name = $"{provider}-t",
            IsActive = true, AuthType = "oauth", Data = data.ToJsonString(),
            CreatedAt = DateTime.UtcNow.ToString("O"), UpdatedAt = DateTime.UtcNow.ToString("O"),
        };
        db.ProviderConnections.Add(conn);
        await db.SaveChangesAsync();
        return conn.Id;
    }

    private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public async Task CodexExport_DownloadsAuthJson()
    {
        var id = await SeedConn("codex", new JsonObject
        {
            ["accessToken"] = "at-cdx", ["refreshToken"] = "rt-cdx", ["accountId"] = "acc-1",
        });
        var c = await Dash();
        var r = await c.PostAsync($"/api/providers/{id}/codex-auth/export", null);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        r.Content.Headers.ContentDisposition?.FileName?.ShouldContain("codex-auth.json");
        var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("tokens").GetProperty("access_token").GetString().ShouldBe("at-cdx");
        doc.RootElement.GetProperty("tokens").GetProperty("refresh_token").GetString().ShouldBe("rt-cdx");
        doc.RootElement.GetProperty("tokens").GetProperty("account_id").GetString().ShouldBe("acc-1");
    }

    [Fact]
    public async Task CodexApplyLocal_WritesThenSkipsFresh()
    {
        var prev = Environment.GetEnvironmentVariable("LLMR_ALLOW_CONFIG_WRITES");
        Environment.SetEnvironmentVariable("LLMR_ALLOW_CONFIG_WRITES", null);
        var authPath = Path.Combine(Home(), ".codex", "auth.json");
        try
        {
            if (File.Exists(authPath)) File.Delete(authPath);
            var id = await SeedConn("codex", new JsonObject { ["accessToken"] = "at-apply" });
            var c = await Dash();

            var r1 = await c.PostAsJsonAsync($"/api/providers/{id}/codex-auth/apply-local", new { });
            r1.StatusCode.ShouldBe(HttpStatusCode.OK);
            var j1 = JsonDocument.Parse(await r1.Content.ReadAsStringAsync());
            j1.RootElement.GetProperty("decision").GetString().ShouldBe("written");
            j1.RootElement.GetProperty("authPath").GetString().ShouldBe(authPath);
            File.Exists(authPath).ShouldBeTrue();
            JsonNode.Parse(await File.ReadAllTextAsync(authPath))!["tokens"]!["access_token"]!
                .GetValue<string>().ShouldBe("at-apply");

            // second call without force -> existing fresh file kept
            var r2 = await c.PostAsJsonAsync($"/api/providers/{id}/codex-auth/apply-local", new { });
            var j2 = JsonDocument.Parse(await r2.Content.ReadAsStringAsync());
            j2.RootElement.GetProperty("decision").GetString().ShouldBe("skipped_present_fresh");
            j2.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLMR_ALLOW_CONFIG_WRITES", prev);
            try { if (File.Exists(authPath)) File.Delete(authPath); } catch { }
            try { if (File.Exists(authPath + ".bak")) File.Delete(authPath + ".bak"); } catch { }
        }
    }

    [Fact]
    public async Task CodexApplyLocal_WritesDisabled_403()
    {
        var prev = Environment.GetEnvironmentVariable("LLMR_ALLOW_CONFIG_WRITES");
        Environment.SetEnvironmentVariable("LLMR_ALLOW_CONFIG_WRITES", "false");
        try
        {
            var id = await SeedConn("codex", new JsonObject { ["accessToken"] = "at-x" });
            var c = await Dash();
            var r = await c.PostAsJsonAsync($"/api/providers/{id}/codex-auth/apply-local", new { });
            r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await r.Content.ReadAsStringAsync()).ShouldContain("writes_disabled");
        }
        finally { Environment.SetEnvironmentVariable("LLMR_ALLOW_CONFIG_WRITES", prev); }
    }

    [Fact]
    public async Task ClaudeApplyLocal_MergesPreservingMcp()
    {
        var prev = Environment.GetEnvironmentVariable("LLMR_ALLOW_CONFIG_WRITES");
        Environment.SetEnvironmentVariable("LLMR_ALLOW_CONFIG_WRITES", null);
        var authPath = Path.Combine(Home(), ".claude", ".credentials.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(authPath)!);
            await File.WriteAllTextAsync(authPath, """{"mcpOAuth":{"x":1}}""");
            var id = await SeedConn("claude", new JsonObject
            {
                ["accessToken"] = "at-cl", ["refreshToken"] = "rt-cl",
                ["providerSpecificData"] = new JsonObject { ["subscriptionType"] = "pro" },
            });
            var c = await Dash();
            var r = await c.PostAsJsonAsync($"/api/providers/{id}/claude-auth/apply-local", new { });
            r.StatusCode.ShouldBe(HttpStatusCode.OK);
            var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            j.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
            j.RootElement.GetProperty("mcpOAuthPreserved").GetBoolean().ShouldBeTrue();
            j.RootElement.GetProperty("savedBakPath").GetString().ShouldBe(authPath + ".bak");
            var doc = JsonNode.Parse(await File.ReadAllTextAsync(authPath))!.AsObject();
            doc["mcpOAuth"].ShouldNotBeNull();
            doc["claudeAiOauth"]!["accessToken"]!.GetValue<string>().ShouldBe("at-cl");
            doc["claudeAiOauth"]!["subscriptionType"]!.GetValue<string>().ShouldBe("pro");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLMR_ALLOW_CONFIG_WRITES", prev);
            try { if (File.Exists(authPath)) File.Delete(authPath); } catch { }
            try { if (File.Exists(authPath + ".bak")) File.Delete(authPath + ".bak"); } catch { }
        }
    }

    [Fact]
    public async Task ClaudeExport_DownloadsCredentials()
    {
        var id = await SeedConn("claude", new JsonObject { ["accessToken"] = "at-cl2" });
        var c = await Dash();
        var r = await c.PostAsync($"/api/providers/{id}/claude-auth/export", null);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        r.Content.Headers.ContentDisposition?.FileName?.ShouldContain("claude-credentials.json");
        var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString().ShouldBe("at-cl2");
    }

    [Fact]
    public async Task ApplyLocal_UnknownId_404()
    {
        var c = await Dash();
        (await c.PostAsJsonAsync("/api/providers/nope/codex-auth/apply-local", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await c.PostAsJsonAsync("/api/providers/nope/claude-auth/apply-local", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Refresh_Contract()
    {
        var c = await Dash();
        // unknown id -> 404
        (await c.PostAsync("/api/providers/nope/refresh", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // conn without refreshToken -> 502 refresh failed
        var id = await SeedConn("codex", new JsonObject { ["accessToken"] = "at-norf" });
        var r = await c.PostAsync($"/api/providers/{id}/refresh", null);
        r.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await r.Content.ReadAsStringAsync()).ShouldContain("refresh failed");
    }
}
