using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Memory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-062: vector memory — embedded fallback store+retrieve, settings, ranking.</summary>
public class VectorMemoryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vm-{Guid.NewGuid():N}.db");
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
    public void LocalEmbed_vetores_similares_para_textos_parecidos()
    {
        var a = VectorMemory.LocalEmbed("postgres connection timeout error");
        var b = VectorMemory.LocalEmbed("postgres connection timeout");
        var c = VectorMemory.LocalEmbed("banana bread recipe");
        double Dot(float[] x, float[] y) => x.Zip(y, (u, v) => (double)u * v).Sum();
        Dot(a, b).ShouldBeGreaterThan(Dot(a, c));
    }

    [Fact]
    public async Task EmbeddedBackend_armazena_e_recupera_item_mais_proximo()
    {
        var hf = _factory.Services.GetRequiredService<IHttpClientFactory>();
        await using var db = Db();
        JsonElement? s = JsonSerializer.Deserialize<JsonElement>("{}");

        await VectorMemory.UpsertAsync(db, hf, s, new VectorMemory.Item("i1", DateTime.UtcNow, "redis cache eviction policy", "infra"));
        await VectorMemory.UpsertAsync(db, hf, s, new VectorMemory.Item("i2", DateTime.UtcNow, "chocolate cake frosting recipe", "food"));

        var hits = await VectorMemory.SearchAsync(db, hf, s, "cache eviction redis", 5);
        hits.ShouldNotBeEmpty();
        hits[0].Item.Id.ShouldBe("i1");
        hits[0].Score.ShouldBeGreaterThan(hits[1].Score);
    }

    [Fact]
    public async Task SettingsQdrant_roundtrip()
    {
        await LoginAsync();
        var put = await _client.PutAsJsonAsync("/api/settings/qdrant",
            new { url = "http://qdrant:6333", collection = "mem", mode = "qdrant" });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        var get = await _client.GetAsync("/api/settings/qdrant");
        var json = await JsonDocument.ParseAsync(await get.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("url").GetString().ShouldBe("http://qdrant:6333");
        json.RootElement.GetProperty("collection").GetString().ShouldBe("mem");
    }

    [Fact]
    public async Task EngineStatus_reporta_modo_e_itens()
    {
        await LoginAsync();
        var hf = _factory.Services.GetRequiredService<IHttpClientFactory>();
        await using var db = Db();
        await VectorMemory.UpsertAsync(db, hf, null, new VectorMemory.Item("x1", DateTime.UtcNow, "foo bar", ""));

        var r = await _client.GetAsync("/api/memory/engine-status");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("mode").GetString().ShouldBe("embedded");
        json.RootElement.GetProperty("items").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task RetrievePreview_retorna_ordem_por_score()
    {
        await LoginAsync();
        var hf = _factory.Services.GetRequiredService<IHttpClientFactory>();
        await using var db = Db();
        JsonElement? s = null;
        await VectorMemory.UpsertAsync(db, hf, s, new VectorMemory.Item("a", DateTime.UtcNow, "database migration checklist", ""));
        await VectorMemory.UpsertAsync(db, hf, s, new VectorMemory.Item("b", DateTime.UtcNow, "pizza dough hydration", ""));
        await VectorMemory.UpsertAsync(db, hf, s, new VectorMemory.Item("c", DateTime.UtcNow, "database rollback plan", ""));

        var r = await _client.PostAsJsonAsync("/api/memory/retrieve-preview", new { q = "database migration", topK = 3 });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        var hits = json.RootElement.GetProperty("hits");
        hits.GetArrayLength().ShouldBe(3);
        var first = hits[0].GetProperty("id").GetString();
        (first == "a" || first == "c").ShouldBeTrue();
        hits[2].GetProperty("id").GetString().ShouldBe("b");
    }

    [Fact]
    public async Task EmbeddingProviders_roundtrip()
    {
        await LoginAsync();
        var put = await _client.PutAsJsonAsync("/api/memory/embedding-providers",
            new { providers = new[] { new { provider = "openai", model = "text-embedding-3-small", dims = 1536 } } });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        var get = await _client.GetAsync("/api/memory/embedding-providers");
        (await get.Content.ReadAsStringAsync()).ShouldContain("text-embedding-3-small");
    }

    [Fact]
    public async Task Health_embedded_sempre_saudavel()
    {
        await LoginAsync();
        var r = await _client.GetAsync("/api/memory/health");
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("healthy").GetBoolean().ShouldBeTrue();
    }
}
