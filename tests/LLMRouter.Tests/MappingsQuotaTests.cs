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

/// <summary>SPEC-083: combo-mappings glob+priority, keyAccess combos, qtSd scoping.</summary>
public class MappingsQuotaTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public MappingsQuotaTests()
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

    private LlmRouterDbContext Db() => new(
        new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    [Fact]
    public void GlobMappingResolvesWithPriority()
    {
        var entries = new[]
        {
            new ComboMappings.Entry("*", "catch-all", 0, true),
            new ComboMappings.Entry("gpt-*", "gpt-combo", 10, true),
            new ComboMappings.Entry("gpt-4*", "disabled-combo", 99, false),
        };
        ComboMappings.Match(entries, "gpt-4o").ShouldBe("gpt-combo");   // disabled 99 skipped
        ComboMappings.Match(entries, "claude-3").ShouldBe("catch-all"); // falls to wildcard
        ComboMappings.Match(entries, "gpt-4o-mini").ShouldBe("gpt-combo");
    }

    [Fact]
    public async Task MappingsCrudRoundTripWithMetadata()
    {
        await _client.PostAsJsonAsync("/api/auth/login", new { password = "x" });
        var post = await _client.PostAsJsonAsync("/api/model-combo-mappings",
            new { model = "sonnet-*", combo = "my-combo", priority = 5, enabled = true });
        post.StatusCode.ShouldBe(HttpStatusCode.OK);

        var get = await _client.GetFromJsonAsync<JsonElement>("/api/model-combo-mappings");
        var entry = get.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("model").GetString() == "sonnet-*");
        entry.GetProperty("combo").GetString().ShouldBe("my-combo");
        entry.GetProperty("priority").GetInt32().ShouldBe(5);
        entry.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        get.GetProperty("mappings").GetProperty("sonnet-*").GetString().ShouldBe("my-combo");
    }

    [Fact]
    public async Task RestrictedKeyAllowsMappedComboName()
    {
        using var db = Db();
        db.Database.EnsureCreated();
        db.Kv.Add(new KvEntry { Scope = "modelComboMappings", Key = "sonnet-*", Value = "my-combo" });
        db.ApiKeys.Add(new ApiKey
        {
            Id = "k1", Key = "restricted-key", AccessRestricted = true, AccessAllow = "my-combo",
        });
        await db.SaveChangesAsync();

        var combo = await ComboMappings.MatchAsync(db, "sonnet-4-5");
        combo.ShouldBe("my-combo");
    }

    [Fact]
    public async Task ModelScopedDailyCapOnlyAppliesToMatchingModels()
    {
        using var db = Db();
        db.Database.EnsureCreated();
        db.Kv.Add(new KvEntry
        {
            Scope = KeyQuota.QuotaScope, Key = "keyA",
            Value = """{"dailyTokens":100,"models":["gpt-*"]}""",
        });
        db.Kv.Add(new KvEntry { Scope = KeyQuota.UsageScope, Key = KeyQuota.DayKey("keyA"), Value = "200" });
        await db.SaveChangesAsync();

        // model matches the glob → cap enforced
        (await KeyQuota.DailyCapExceededAsync(db, "keyA", "gpt-4o")).ShouldBeTrue();
        // different family → cap skipped entirely
        (await KeyQuota.DailyCapExceededAsync(db, "keyA", "claude-sonnet")).ShouldBeFalse();
        // no model arg → behaves like global cap
        (await KeyQuota.DailyCapExceededAsync(db, "keyA")).ShouldBeTrue();

        // per-model counters credited separately (qtSd/)
        await KeyQuota.CreditDailyAsync(db, "keyA", 50, "gpt-4o");
        await db.SaveChangesAsync();
        var perModel = await KeyQuota.ModelUsageAsync(db, "keyA");
        perModel["gpt-4o"].ShouldBe(50);
        (await KeyQuota.DailyUsedAsync(db, "keyA")).ShouldBe(250);
    }
}
