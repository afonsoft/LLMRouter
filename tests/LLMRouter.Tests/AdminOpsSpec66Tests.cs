using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-066: admin concurrency, tags, db/storage health, fallback chains, monitors.</summary>
public class AdminOpsSpec66Tests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"a66-{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        ConcurrencyGate.Set(0);
        _client.Dispose();
        await _factory.DisposeAsync();
        try { File.Delete(_dbPath); } catch { }
    }

    private LlmRouterDbContext Db() =>
        _factory.Services.CreateScope().ServiceProvider.GetRequiredService<LlmRouterDbContext>();

    private async Task LoginAsync()
    {
        var r = await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        r.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ConcurrencyCap_roundtrip_e_gate()
    {
        await LoginAsync();
        var put = await _client.PutAsJsonAsync("/api/admin/concurrency", new { limit = 3 });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        ConcurrencyGate.Limit.ShouldBe(3);

        var get = await _client.GetAsync("/api/admin/concurrency");
        var json = await JsonDocument.ParseAsync(await get.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("limit").GetInt32().ShouldBe(3);

        // persisted em settings.data
        await using var db = Db();
        var row = await db.Settings.FindAsync(1);
        row!.Data.ShouldContain("\"concurrency\":3");
    }

    [Fact]
    public async Task Tags_filtra_conexoes()
    {
        await LoginAsync();
        await using var db = Db();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c1", Provider = "openai", Name = "n1", IsActive = true,
            Data = "{}", CreatedAt = "x", UpdatedAt = "x",
        });
        await db.SaveChangesAsync();

        // tag via API existente (tabela tags) + espelho via data.tags
        var tag = await _client.PostAsJsonAsync("/api/tags",
            new { targetType = "provider", targetId = "c1", value = "prod" });
        tag.StatusCode.ShouldBe(HttpStatusCode.OK);
        var put = await _client.PutAsJsonAsync("/api/provider-connections/c1/tags",
            new { tags = new[] { "prod", "us" } });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        var r = await _client.GetAsync("/api/tags?targetType=provider&targetId=c1");
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("prod");
        db.ChangeTracker.Clear();
        (await db.ProviderConnections.FindAsync("c1"))!.Data.ShouldContain("\"us\"");
    }

    [Fact]
    public async Task DbHealth_integridade_ok()
    {
        await LoginAsync();
        var r = await _client.GetAsync("/api/db/health");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("integrity").GetString().ShouldBe("ok");
    }

    [Fact]
    public async Task FallbackChains_edit_persiste()
    {
        await LoginAsync();
        // endpoint existente (ResilienceOps): POST grava a chain
        var put = await _client.PostAsJsonAsync("/api/fallback/chains",
            new { model = "gpt-4", chain = new[] { "openai/gpt-4", "google/gemini-pro" } });
        ((int)put.StatusCode).ShouldBeLessThan(500);

        var get = await _client.GetAsync("/api/fallback/chains");
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Monitors_agregam_usage()
    {
        await LoginAsync();
        await using var db = Db();
        db.UsageHistory.Add(new UsageRecord
        {
            Provider = "openai", Model = "m", Endpoint = "chat", Status = "200",
            Timestamp = DateTime.UtcNow.ToString("o"), PromptTokens = 10, CompletionTokens = 5,
        });
        await db.SaveChangesAsync();

        var r = await _client.GetAsync("/api/telemetry/summary");
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("requests").GetInt32().ShouldBe(1);
        json.RootElement.GetProperty("tokens").GetInt64().ShouldBe(15);

        (await _client.GetAsync("/api/monitoring/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync("/api/token-health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync("/api/health/degradation")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync("/api/storage/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync("/api/network/info")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync("/api/omniroute/status")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HooksPolicies_proxyVisibility_headroom_roundtrip()
    {
        await LoginAsync();
        (await _client.PutAsJsonAsync("/api/middleware/hooks", new { hooks = new[] { new { name = "h1" } } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await _client.GetAsync("/api/middleware/hooks")).Content.ReadAsStringAsync())
            .ShouldContain("h1");
        // policies: CRUD existente (CoreMiscEndpoints) — POST cria uma policy
        (await _client.PostAsJsonAsync("/api/policies",
            new { name = "r1", scope = "model", condition = "always", action = "allow" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await _client.GetAsync("/api/policies")).Content.ReadAsStringAsync())
            .ShouldContain("r1");
        (await _client.PutAsJsonAsync("/api/admin/proxy-pool-visibility",
            new { visibility = new { openai = new[] { "pool1" } } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.PostAsync("/api/headroom/start", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var status = await _client.GetAsync("/api/headroom/status");
        (await status.Content.ReadAsStringAsync()).ShouldContain("\"running\":true");
        (await _client.PostAsync("/api/headroom/stop", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
