using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using LLMRouter.Core.Data;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>
/// SPEC-064: /v1 extras — me/status, registered-keys, quotas/check,
/// combos, classify, explain/routing, auto-combo candidates, issues/report,
/// session-leases, management proxies e stubs de compatibilidade.
/// </summary>
public class V1ExtrasTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"v1extras-{Guid.NewGuid():N}.db");
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

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string? key = null, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (key is not null) req.Headers.Authorization = new("Bearer", key);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await _client.SendAsync(req);
    }

    private async Task SeedKey(string id = "k1", string key = "sk-aaaa-111122223333")
    {
        await using var db = Db();
        db.ApiKeys.Add(new ApiKey { Id = id, Key = key, CreatedAt = "x" });
        await db.SaveChangesAsync();
    }

    private async Task LoginAsync()
    {
        var r = await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        r.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task MeStatus_com_chave_valida_retorna_identidade()
    {
        await SeedKey();
        var r = await Send(HttpMethod.Get, "/v1/me/status", "sk-aaaa-111122223333");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("authenticated").GetBoolean().ShouldBeTrue();
        json.RootElement.GetProperty("keyId").GetString().ShouldBe("k1");
        json.RootElement.GetProperty("masked").GetString().ShouldContain("…");
    }

    [Fact]
    public async Task MeStatus_sem_chave_retorna_401()
    {
        var r = await Send(HttpMethod.Get, "/v1/me/status");
        r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RegisteredKeys_mascara_chaves()
    {
        await using var db = Db();
        db.ApiKeys.Add(new ApiKey { Id = "k1", Key = "sk-aaaa-111122223333", CreatedAt = "x" });
        db.ApiKeys.Add(new ApiKey { Id = "k2", Key = "sk-bbbb-444455556666", CreatedAt = "x" });
        await db.SaveChangesAsync();

        var r = await Send(HttpMethod.Get, "/v1/registered-keys", "sk-aaaa-111122223333");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldNotContain("111122223333");
        body.ShouldNotContain("444455556666");
        body.ShouldContain("\"own\":true");
    }

    [Fact]
    public async Task QuotasCheck_reflete_cap_diario()
    {
        await SeedKey("k1", "sk-q-1234567890ab");
        var r = await Send(HttpMethod.Get, "/v1/quotas/check?model=gpt-4", "sk-q-1234567890ab");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("allowed").GetBoolean().ShouldBeTrue();
        json.RootElement.GetProperty("model").GetString().ShouldBe("gpt-4");
    }

    [Fact]
    public async Task AccountsLimits_retorna_quota_e_rate_limits()
    {
        await SeedKey("k1", "sk-al-1234567890a");
        var r = await Send(HttpMethod.Get, "/v1/accounts/k1/limits", "sk-al-1234567890a");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("id").GetString().ShouldBe("k1");
        json.RootElement.GetProperty("quota").GetProperty("dailyTokens").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Combos_lista_combos_configurados()
    {
        await SeedKey("k1", "sk-c-1234567890ab");
        await using var db = Db();
        db.Combos.Add(new Combo { Id = "cb1", Name = "best", Models = "[\"openai/gpt-4\"]", CreatedAt = "x", UpdatedAt = "x" });
        await db.SaveChangesAsync();

        var r = await Send(HttpMethod.Get, "/v1/combos", "sk-c-1234567890ab");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("\"best\"");
        body.ShouldContain("openai/gpt-4");
    }

    [Fact]
    public async Task Classify_retorna_combo_sugerido()
    {
        await SeedKey("k1", "sk-cl-1234567890");
        await using var db = Db();
        db.Combos.Add(new Combo { Id = "cb1", Name = "code-combo", Models = "[\"openai/codex\"]", CreatedAt = "x", UpdatedAt = "x" });
        db.Combos.Add(new Combo { Id = "cb2", Name = "vision-combo", Models = "[\"openai/gpt-4v\"]", CreatedAt = "x", UpdatedAt = "x" });
        await db.SaveChangesAsync();

        var r = await Send(HttpMethod.Post, "/v1/classify", "sk-cl-1234567890",
            new { prompt = "look at this image and describe it" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("combo").GetString().ShouldBe("vision-combo");
    }

    [Fact]
    public async Task ExplainRouting_mostra_cadeia_de_resolucao()
    {
        await LoginAsync();
        var r = await Send(HttpMethod.Post, "/v1/explain/routing", null, new { model = "auto/best" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("model").GetString().ShouldBe("auto/best");
        json.RootElement.GetProperty("chain").GetArrayLength().ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task IssuesReport_grava_auditoria()
    {
        await SeedKey("k1", "sk-i-1234567890ab");
        var r = await Send(HttpMethod.Post, "/v1/issues/report", "sk-i-1234567890ab",
            new { message = "timeout em provider X" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db2 = Db();
        var log = await db2.Kv.FindAsync("audit", "log");
        log.ShouldNotBeNull();
        log!.Value.ShouldContain("issue.report");
    }

    [Fact]
    public async Task ManagementProxies_crud_minimo()
    {
        await LoginAsync();
        var create = await Send(HttpMethod.Post, "/v1/management/proxies", null, new { url = "http://1.2.3.4:8080" });
        create.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await JsonDocument.ParseAsync(await create.Content.ReadAsStreamAsync());
        var id = json.RootElement.GetProperty("id").GetString()!;

        var list = await _client.GetAsync("/v1/management/proxies");
        (await list.Content.ReadAsStringAsync()).ShouldContain(id);

        var del = await _client.DeleteAsync($"/v1/management/proxies/{id}");
        del.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CompatStubs_respondem_com_auth()
    {
        await LoginAsync();
        (await _client.GetAsync("/v1/antigravity")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync("/v1/muse-code/models")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync("/v1/video-bridge/drilldown")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync("/v1/provider-plugin-manifest")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync("/v1/session-leases")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync("/v1/auto-combo/best/candidates")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
