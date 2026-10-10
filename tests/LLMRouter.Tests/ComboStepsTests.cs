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

/// <summary>SPEC-082: combo steps 2.0 — combo-ref, wildcard, invariants, autoPromote.</summary>
public class ComboStepsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ComboStepsTests()
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
    public async Task NestedComboRefExpandsWithCycleGuard()
    {
        using var db = Db();
        db.Database.EnsureCreated();
        db.Combos.AddRange(
            new Combo { Id = "a", Name = "inner", Models = """["openai/gpt-4o"]""" },
            new Combo { Id = "b", Name = "outer", Models = """["inner","anthropic/claude-sonnet-4-5"]""" },
            new Combo { Id = "c", Name = "self", Models = """["self","openai/gpt-4o-mini"]""" });
        await db.SaveChangesAsync();

        var reg = new Core.Registry.ProviderRegistry();
        var outer = await ComboSteps.ExpandAsync(db, reg,
            (await db.Combos.FindAsync("b"))!.Models, ["outer"]);
        outer.Models.ShouldBe(new[] { "openai/gpt-4o", "anthropic/claude-sonnet-4-5" });

        // self-referencing combo doesn't infinite-loop
        var self = await ComboSteps.ExpandAsync(db, reg,
            (await db.Combos.FindAsync("c"))!.Models, ["self"]);
        self.Models.ShouldContain("openai/gpt-4o-mini");
    }

    [Fact]
    public async Task WildcardStepExpandsProviderModels()
    {
        using var db = Db();
        db.Database.EnsureCreated();
        var reg = new Core.Registry.ProviderRegistry();
        var ex = await ComboSteps.ExpandAsync(db, reg, """["openai/gpt-4o*","openai/gpt-4o-mini"]""", ["x"]);
        ex.Models.Count.ShouldBeGreaterThanOrEqualTo(2);
        ex.Models.ShouldAllBe(m => m.StartsWith("openai/"));
        ex.Models.ShouldContain("openai/gpt-4o");
    }

    [Fact]
    public async Task InvariantRejectsViolatingStep()
    {
        await _client.PostAsJsonAsync("/api/auth/login", new { password = "x" });
        var resp = await _client.PostAsJsonAsync("/api/combos", new
        {
            name = "fam-combo",
            allowedModelFamilies = new[] { "gpt" },
            models = new[] { "anthropic/claude-sonnet-4-5" },
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).ShouldContain("violates its invariant");

        var ok = await _client.PostAsJsonAsync("/api/combos", new
        {
            name = "fam-ok",
            allowedModelFamilies = new[] { "gpt" },
            models = new[] { "openai/gpt-4o" },
        });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AutoPromoteReordersAfterSuccess()
    {
        using var db = Db();
        db.Database.EnsureCreated();
        db.Combos.Add(new Combo
        {
            Id = "p", Name = "promote", Kind = "fallback",
            Models = """["openai/gpt-4o","anthropic/claude-sonnet-4-5"]""",
        });
        db.Settings.Add(new SettingRow { Data = """{"comboAutoPromoteEnabled":true}""" });
        await db.SaveChangesAsync();

        var sdata = JsonDocument.Parse("""{"comboAutoPromoteEnabled":true}""").RootElement;
        await ComboSteps.AutoPromoteAsync(db, sdata, "promote", "anthropic/claude-sonnet-4-5");

        var combo = await db.Combos.FindAsync("p");
        using var doc = JsonDocument.Parse(combo!.Models);
        doc.RootElement[0].GetString().ShouldBe("anthropic/claude-sonnet-4-5");
    }

    [Fact]
    public async Task QuotaOnlyStepsAppendAfterPrimary()
    {
        using var db = Db();
        db.Database.EnsureCreated();
        var reg = new Core.Registry.ProviderRegistry();
        var ex = await ComboSteps.ExpandAsync(db, reg,
            """["openai/gpt-4o",{"kind":"model","model":"anthropic/claude-sonnet-4-5","fallbackOnlyOnQuotaExhaustion":true}]""",
            ["x"]);
        ex.Models.ShouldBe(new[] { "openai/gpt-4o" });
        ex.QuotaOnly.ShouldBe(new[] { "anthropic/claude-sonnet-4-5" });
    }
}
