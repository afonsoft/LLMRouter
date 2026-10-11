using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-112: catálogo público OpenRouter-like + signup→key.</summary>
public class Spec112PublicCatalogTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public Spec112PublicCatalogTests()
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

    private async Task LoginAsync() =>
        (await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

    private async Task PutSettingsAsync(object patch) =>
        (await _client.PutAsJsonAsync("/api/settings", patch))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

    private async Task SeedConnectionAsync()
    {
        await using var ctx = new LlmRouterDbContext(
            new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        ctx.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c1", Provider = "openai", AuthType = "apikey", IsActive = true,
            Data = "{}", CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01",
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Public_catalog_returns_openrouter_shape_with_markup_pricing()
    {
        await LoginAsync();
        await SeedConnectionAsync();
        await PutSettingsAsync(new
        {
            markup = new { defaultPercent = 50.0 },
        });

        var anon = _factory.CreateClient(); // sem cookie: endpoint é público
        var resp = await anon.GetAsync("/api/public/models");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        data.GetArrayLength().ShouldBeGreaterThan(0);
        var first = data[0];
        first.GetProperty("id").GetString().ShouldContain("/");
        first.TryGetProperty("context_length", out _).ShouldBeTrue();
        first.TryGetProperty("capabilities", out _).ShouldBeTrue();
        if (first.TryGetProperty("pricing", out var pr) && pr.ValueKind == JsonValueKind.Object)
        {
            pr.GetProperty("currency").GetString().ShouldBe("USD");
            pr.TryGetProperty("prompt", out _).ShouldBeTrue();
        }
        anon.Dispose();
    }

    [Fact]
    public async Task Public_catalog_off_returns_404()
    {
        await LoginAsync();
        await PutSettingsAsync(new { publicCatalog = false });
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/public/models")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        anon.Dispose();
    }

    [Fact]
    public async Task Signup_disabled_returns_403_enabled_creates_user_key_credit()
    {
        var anon = _factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/signup", new { email = "a@b.dev" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await LoginAsync();
        await PutSettingsAsync(new { signup = new { enabled = true, initialCreditUsd = 2.5 } });

        var resp = await anon.PostAsJsonAsync("/api/signup", new { email = "buyer@shop.dev", name = "B" });
        resp.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var vk = body.GetProperty("key").GetString()!;
        vk.ShouldStartWith("vk-");
        body.GetProperty("creditUsd").GetDouble().ShouldBe(2.5);

        // a vk emitida autentica no gateway
        var req = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        req.Headers.Add("Authorization", $"Bearer {vk}");
        (await anon.SendAsync(req)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // ledger tem o topup
        var ledger = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/credit-ledger?userId={body.GetProperty("userId").GetString()}");
        ledger.GetProperty("entries")[0].GetProperty("balanceAfter").GetDouble().ShouldBe(2.5);
        anon.Dispose();
    }

    [Fact]
    public async Task Signup_duplicate_email_returns_409()
    {
        await LoginAsync();
        await PutSettingsAsync(new { signup = new { enabled = true } });
        var anon = _factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/signup", new { email = "dup@x.dev" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await anon.PostAsJsonAsync("/api/signup", new { email = "dup@x.dev" }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        anon.Dispose();
    }

    [Fact]
    public async Task Markup_per_model_overrides_default()
    {
        await LoginAsync();
        await SeedConnectionAsync();
        await PutSettingsAsync(new
        {
            markup = new { defaultPercent = 10.0, perModel = new Dictionary<string, double> { ["openai/gpt-4o"] = 100.0 } },
            catalogUserPricing = new Dictionary<string, object>
            {
                ["openai/gpt-4o"] = new { input = 5.0, output = 15.0 },
            },
        });

        var anon = _factory.CreateClient();
        var body = await (await anon.GetAsync("/api/public/models")).Content.ReadFromJsonAsync<JsonElement>();
        var gpt = body.GetProperty("data").EnumerateArray()
            .FirstOrDefault(m => m.GetProperty("id").GetString() == "openai/gpt-4o");
        if (gpt.ValueKind == JsonValueKind.Object && gpt.TryGetProperty("pricing", out var pr)
            && pr.ValueKind == JsonValueKind.Object)
            pr.GetProperty("prompt").GetDouble().ShouldBe(10.0 / 1_000_000.0, 1e-12); // 5 × (1+100%)
        anon.Dispose();
    }
}
