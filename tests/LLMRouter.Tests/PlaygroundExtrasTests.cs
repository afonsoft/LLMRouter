using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-043: simulate-route resolution + presets CRUD.</summary>
public class PlaygroundExtrasTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public PlaygroundExtrasTests()
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

    private async Task SeedConnectionAsync()
    {
        await using var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.EnsureCreated();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c1", Provider = "openai", Name = "main", IsActive = true,
            Data = "{}", CreatedAt = "x", UpdatedAt = "x",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Simulate_route_resolves_provider_and_masks_headers()
    {
        await LoginAsync();
        await SeedConnectionAsync();

        var r = await _client.PostAsJsonAsync("/api/playground/simulate-route",
            new { model = "openai/gpt-4o", body = new { model = "openai/gpt-4o", messages = new[] { new { role = "user", content = "hi" } } } });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        var targets = body.GetProperty("targets").EnumerateArray().ToList();
        targets.Count.ShouldBeGreaterThanOrEqualTo(1);
        targets[0].GetProperty("provider").GetString().ShouldBe("openai");
        targets[0].GetProperty("upstreamModel").GetString().ShouldBe("gpt-4o");
        targets[0].GetProperty("connectionId").GetString().ShouldBe("c1");

        var call = body.GetProperty("wouldBeCall");
        call.ValueKind.ShouldBe(JsonValueKind.Object);
        // auth headers masked — a dry run must never leak secrets
        call.GetProperty("headers").GetRawText().ShouldNotContain("sk-");
    }

    [Fact]
    public async Task Presets_crud_round_trip()
    {
        await LoginAsync();
        var create = await _client.PostAsJsonAsync("/api/playground/presets",
            new { name = "p1", model = "openai/gpt-4o", @params = new { temperature = 0.2 } });
        create.StatusCode.ShouldBe(HttpStatusCode.OK);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("preset").GetProperty("id").GetString()!;

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/playground/presets");
        list.GetProperty("presets").EnumerateArray().ShouldContain(x => x.GetProperty("id").GetString() == id);

        var put = await _client.PutAsJsonAsync($"/api/playground/presets/{id}", new { name = "p2" });
        (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("preset").GetProperty("name").GetString().ShouldBe("p2");

        (await _client.DeleteAsync($"/api/playground/presets/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
