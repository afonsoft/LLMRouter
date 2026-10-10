using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-085: discovery scan/results/verify + combo presets.</summary>
public class DiscoveryPresetsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public DiscoveryPresetsTests()
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

    private async Task Login() => await _client.PostAsJsonAsync("/api/auth/login", new { password = "x" });

    [Fact]
    public async Task DiscoveryScanPersistsAndVerifyWorks()
    {
        await Login();
        var scan = await _client.PostAsJsonAsync("/api/discovery/scan", new { providerId = "ollama" });
        scan.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await scan.Content.ReadFromJsonAsync<JsonElement>();
        var first = body.GetProperty("results")[0];
        first.GetProperty("providerId").GetString().ShouldBe("ollama");
        var id = first.GetProperty("id").GetString()!;

        var results = await _client.GetFromJsonAsync<JsonElement>("/api/discovery/results?providerId=ollama");
        results.GetProperty("results").EnumerateArray()
            .Any(r => r.GetProperty("id").GetString() == id).ShouldBeTrue();

        var verify = await _client.PostAsync($"/api/discovery/verify/{id}", null);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await verify.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("result").GetProperty("status").GetString().ShouldBe("verified");
    }

    [Fact]
    public async Task DiscoveryScanUnknownProviderWritesStubRow()
    {
        await Login();
        var scan = await _client.PostAsJsonAsync("/api/discovery/scan", new { providerId = "unknownxyz" });
        scan.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await scan.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("results")[0].GetProperty("notes").GetString().ShouldContain("Stub scan");
        body.GetProperty("results")[0].GetProperty("status").GetString().ShouldBe("pending");
    }

    [Fact]
    public async Task ComboPresetsSeedsClaudeAliases()
    {
        await Login();
        var resp = await _client.PostAsJsonAsync("/api/combos/presets", new { source = "claude" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("created").GetInt32().ShouldBeGreaterThan(0);

        using var db = new LlmRouterDbContext(
            new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        var names = await db.Combos.Select(c => c.Name).ToListAsync();
        names.ShouldContain("default");
        names.ShouldContain("opusplan");
        var def = await db.Combos.FirstAsync(c => c.Name == "default");
        using var doc = JsonDocument.Parse(def.Models);
        doc.RootElement[0].GetString().ShouldStartWith("cc/");

        // second run skips everything
        var again = await _client.PostAsJsonAsync("/api/combos/presets", new { source = "claude" });
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task ComboPresetsSeedsCursorModels()
    {
        await Login();
        var resp = await _client.PostAsJsonAsync("/api/combos/presets", new { source = "cursor" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("created").GetInt32().ShouldBeGreaterThan(0);

        using var db = new LlmRouterDbContext(
            new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        var any = await db.Combos.AnyAsync(c => c.Models.Contains("\"cu/"));
        any.ShouldBeTrue();
    }
}
