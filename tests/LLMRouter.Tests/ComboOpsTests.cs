using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-069: combo ops — pool test, persisted reorder, combo-defaults.</summary>
public class ComboOpsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public ComboOpsTests()
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

    private async Task<string> MakeCombo(HttpClient c, string name, params string[] models)
    {
        var r = await c.PostAsJsonAsync("/api/combos", new { name, kind = "fallback", models });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await r.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("combo").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task TestPool_reports_per_model_connections()
    {
        await using (var db = Db())
        {
            db.EnsureCreated();
            db.ProviderConnections.Add(new ProviderConnection
            {
                Id = "c1", Provider = "openai", AuthType = "apikey", Name = "t",
                Data = """{"apiKey":"sk-fake"}""", CreatedAt = "x", UpdatedAt = "x",
            });
            await db.SaveChangesAsync();
        }
        var c = LoginClient();
        var id = await MakeCombo(c, "tc1", "openai/gpt-4o", "nope/m");
        var r = await c.PostAsJsonAsync($"/api/combos/{id}/test", new { });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var pool = (await r.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("pool").EnumerateArray().ToList();
        pool.Count.ShouldBe(2);
        pool[0].GetProperty("model").GetString().ShouldBe("openai/gpt-4o");
        pool[0].GetProperty("connections").GetInt32().ShouldBe(1);
        pool[0].GetProperty("ok").GetBoolean().ShouldBeTrue();
        pool[1].GetProperty("connections").GetInt32().ShouldBe(0);
        pool[1].GetProperty("ok").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Reorder_persists_combo_order()
    {
        await using (var db = Db()) db.EnsureCreated();
        var c = LoginClient();
        var a = await MakeCombo(c, "ro-a");
        var b = await MakeCombo(c, "ro-b");
        var r = await c.PostAsJsonAsync("/api/combos/reorder", new { ids = new[] { b, a } });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var list = (await (await c.GetAsync("/api/combos")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("combos").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()).ToList();
        list.IndexOf(b).ShouldBeLessThan(list.IndexOf(a));
    }

    [Fact]
    public async Task ComboDefaults_roundtrip()
    {
        await using (var db = Db()) db.EnsureCreated();
        var c = LoginClient();
        var r = await c.PutAsJsonAsync("/api/settings/combo-defaults",
            new { defaultCombo = "test-combo", handoffModel = "openai/gpt-4o" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var d = await (await c.GetAsync("/api/settings/combo-defaults"))
            .Content.ReadFromJsonAsync<JsonElement>();
        d.GetProperty("defaultCombo").GetString().ShouldBe("test-combo");
        d.GetProperty("handoffModel").GetString().ShouldBe("openai/gpt-4o");
    }
}

public class ComboPricingTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public ComboPricingTests()
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

    private HttpClient LoginClient()
    {
        var c = _factory.CreateClient();
        c.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Result
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return c;
    }

    private static string NewId(HttpClient c)
    {
        var r = c.PostAsJsonAsync("/api/combos",
            new { name = $"pc-{Guid.NewGuid():N}"[..14], kind = "fallback", models = new[] { "x/y" } }).Result;
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return r.Content.ReadFromJsonAsync<JsonElement>().Result
            .GetProperty("combo").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Pricing_RoundTrips_AndClears()
    {
        var c = LoginClient(); var id = NewId(c);
        var put = await c.PutAsJsonAsync($"/api/combos/{id}", new { pricing = new { input = 3.0, output = 9.0 } });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var combo = (await (await c.GetAsync("/api/combos")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("combos").EnumerateArray().First(x => x.GetProperty("id").GetString() == id);
        combo.GetProperty("pricing").GetProperty("input").GetDouble().ShouldBe(3.0);
        combo.GetProperty("pricing").GetProperty("output").GetDouble().ShouldBe(9.0);
        (await c.PutAsJsonAsync($"/api/combos/{id}", new { pricing = (object?)null })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var cleared = (await (await c.GetAsync("/api/combos")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("combos").EnumerateArray().First(x => x.GetProperty("id").GetString() == id);
        cleared.GetProperty("pricing").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Pricing_RejectsBadShape()
    {
        var c = LoginClient(); var id = NewId(c);
        var bad = await c.PutAsJsonAsync($"/api/combos/{id}", new { pricing = new { input = "x" } });
        ((int)bad.StatusCode).ShouldBe(400);
    }

    [Fact]
    public void PricingJsonParses_Validates()
    {
        LLMRouter.Core.Gateway.GatewayEngine.PricingJsonParses("{\"input\":1.5,\"output\":2}", out var p)
            .ShouldBeTrue();
        p.input.ShouldBe(1.5); p.output.ShouldBe(2.0);
        LLMRouter.Core.Gateway.GatewayEngine.PricingJsonParses("{\"input\":\"x\"}", out _).ShouldBeFalse();
        LLMRouter.Core.Gateway.GatewayEngine.PricingJsonParses("not-json", out _).ShouldBeFalse();
    }
}
