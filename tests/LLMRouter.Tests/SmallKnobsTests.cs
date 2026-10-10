using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-081: resilience/reset, free-models, disabled models, context-window, dead config keys.</summary>
public class SmallKnobsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SmallKnobsTests()
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
        (await _client.PostAsJsonAsync("/api/auth/login", new { password = "x" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

    private LlmRouterDbContext Db() => new(
        new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    [Fact]
    public async Task ResilienceResetClearsAllState()
    {
        await LoginAsync();
        Core.Resilience.CooldownTracker.ReportFailure("c1");
        Core.Resilience.CooldownTracker.ReportFailure("c1");
        Core.Resilience.CooldownTracker.ReportFailure("c1");
        Core.Resilience.ProviderBreaker.ReportFailure("openai");
        Core.Resilience.ModelLockout.Lock("openai", "c1", "gpt-4");
        var resp = await _client.PostAsync("/api/resilience/reset", null);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        Core.Resilience.CooldownTracker.IsCooling("c1").ShouldBeFalse();
        Core.Resilience.ModelLockout.IsLocked("openai", "c1", "gpt-4").ShouldBeFalse();
    }

    [Fact]
    public async Task FreeModelsReturnsCatalog()
    {
        await LoginAsync();
        var doc = await _client.GetFromJsonAsync<JsonElement>("/api/free-models");
        doc.GetProperty("curatedAt").GetString().ShouldBe("2026-09-12");
        var models = doc.GetProperty("models");
        models.GetArrayLength().ShouldBeGreaterThan(100);
        models[0].GetProperty("provider").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task DisabledModelsHiddenFromModelListAndResolution()
    {
        await LoginAsync();
        using (var db = Db())
        {
            db.Database.EnsureCreated();
            db.ProviderConnections.Add(new ProviderConnection
            {
                Id = "c1", Provider = "openai", Name = "o", IsActive = true, Data = "{}",
            });
            db.Combos.Add(new Combo { Id = "cb1", Name = "my-combo", Models = "[\"openai/gpt-4o\",\"openai/gpt-4o-mini\"]" });
            await db.SaveChangesAsync();
        }

        // disable gpt-4o
        var put = await _client.PutAsJsonAsync("/api/models/disabled/openai", new { models = new[] { "gpt-4o" } });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        // /api/models no longer lists it
        var list = await _client.GetFromJsonAsync<JsonElement>("/api/models");
        list.GetProperty("models").EnumerateArray()
            .Select(m => m.GetProperty("model").GetString())
            .ShouldNotContain("gpt-4o");

        // GET map shows it
        var map = await _client.GetFromJsonAsync<JsonElement>("/api/models/disabled");
        map.GetProperty("disabled").GetProperty("openai")[0].GetString().ShouldBe("gpt-4o");

        // delete re-enables
        (await _client.DeleteAsync("/api/models/disabled/openai")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var list2 = await _client.GetFromJsonAsync<JsonElement>("/api/models");
        list2.GetProperty("models").EnumerateArray()
            .Select(m => m.GetProperty("model").GetString())
            .ShouldContain("gpt-4o");
    }

    [Fact]
    public async Task ComboContextWindowComputesMemberBounds()
    {
        await LoginAsync();
        using (var db = Db())
        {
            db.Database.EnsureCreated();
            db.Combos.Add(new Combo { Id = "cb1", Name = "ctx", Models = "[\"openai/gpt-4o\",\"openai/gpt-4o-mini\"]" });
            await db.SaveChangesAsync();
        }
        var doc = await _client.GetFromJsonAsync<JsonElement>("/api/combos/ctx/context-window");
        doc.GetProperty("models").GetInt32().ShouldBeGreaterThan(0);
        doc.GetProperty("min").GetInt32().ShouldBeLessThanOrEqualTo(doc.GetProperty("max").GetInt32());
    }

    [Fact]
    public async Task DeadConfigKeysListedAndPruned()
    {
        await LoginAsync();
        using (var db = Db())
        {
            db.Database.EnsureCreated();
            var row = await db.Settings.FirstOrDefaultAsync();
            if (row is null) db.Settings.Add(new SettingRow { Data = """{"autoRouter":{},"unknownKey":1,"anotherDead":{}}""" });
            else row.Data = """{"autoRouter":{},"unknownKey":1,"anotherDead":{}}""";
            await db.SaveChangesAsync();
        }
        var doc = await _client.GetFromJsonAsync<JsonElement>("/api/settings/dead-config-keys");
        var dead = doc.GetProperty("dead").EnumerateArray().Select(e => e.GetString()).ToList();
        dead.ShouldContain("unknownKey");
        dead.ShouldContain("anotherDead");
        dead.ShouldNotContain("autoRouter");

        var del = await _client.DeleteAsync("/api/settings/dead-config-keys");
        del.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var db = Db())
            (await db.Settings.FirstAsync()).Data.ShouldNotContain("unknownKey");
    }
}
