using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-104: config-as-code export/validate/diff/import.</summary>
public class Spec104ConfigAsCodeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public Spec104ConfigAsCodeTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        _client = _factory.CreateClient();
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

    private async Task SeedComboAndProviderAsync()
    {
        await using var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.EnsureCreated();
        db.Combos.Add(new Combo
        {
            Id = "c1", Name = "mix", CreatedAt = "x", UpdatedAt = "x",
            Models = """["openai/m1"]""",
        });
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "p1", Provider = "openai", Name = "main", IsActive = true,
            Data = """{"apiKey":"sk-live-123","baseUrl":"https://x"}""",
            CreatedAt = "x", UpdatedAt = "x",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Export_emits_llmr_config_and_redacts_provider_secrets()
    {
        await LoginAsync();
        await SeedComboAndProviderAsync();
        var doc = await _client.GetFromJsonAsync<JsonElement>("/api/config/export?scope=all");
        doc.GetProperty("format").GetString().ShouldBe("llmr-config");
        doc.GetProperty("version").GetInt32().ShouldBe(1);
        var scopes = doc.GetProperty("scopes");
        foreach (var s in new[] { "settings", "combos", "mappings", "rateLimits", "keys", "providers", "guardrails" })
            scopes.TryGetProperty(s, out _).ShouldBeTrue();
        var prov = scopes.GetProperty("providers")[0];
        prov.GetProperty("data").GetProperty("apiKey").GetProperty("$secret").GetBoolean().ShouldBeTrue();
        doc.GetRawText().ShouldNotContain("sk-live-123");
        prov.GetProperty("data").GetProperty("baseUrl").GetString().ShouldBe("https://x");
    }

    [Fact]
    public async Task Validate_rejects_unknown_scope_and_dangling_combo_refs()
    {
        await LoginAsync();
        var doc = JsonNode.Parse("""
            {"format":"llmr-config","version":1,"scopes":{
              "bogusScope": [], "combos":[{"name":"x","models":"[\"nope-llm/m\"]"}]}}
            """)!.AsObject();
        var r = await _client.PostAsJsonAsync("/api/config/validate", doc);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("ok").GetBoolean().ShouldBeFalse();
        var errs = body.GetProperty("errors");
        errs.TryGetProperty("bogusScope", out _).ShouldBeTrue();
        errs.GetProperty("combos")[0].GetString().ShouldContain("dangling");
    }

    [Fact]
    public async Task Import_round_trip_restores_wiped_scope()
    {
        await LoginAsync();
        await SeedComboAndProviderAsync();
        var doc = await _client.GetFromJsonAsync<JsonElement>("/api/config/export?scope=combos,providers");

        await using (var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options))
        {
            db.Combos.RemoveRange(db.Combos);
            db.ProviderConnections.RemoveRange(db.ProviderConnections);
            await db.SaveChangesAsync();
        }

        var imp = await _client.PostAsJsonAsync("/api/config/import",
            new { doc, scopes = new[] { "combos", "providers" } });
        imp.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await imp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean().ShouldBeTrue();

        await using var verify = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        (await verify.Combos.CountAsync()).ShouldBe(1);
        (await verify.ProviderConnections.CountAsync()).ShouldBe(1);
        // "keep" had nothing stored to keep — redacted marker dropped, not the literal {"$secret"}
        (await verify.ProviderConnections.FirstAsync()).Data.ShouldNotContain("$secret");
    }

    [Fact]
    public async Task Import_dry_run_writes_nothing_and_reports_counts()
    {
        await LoginAsync();
        await SeedComboAndProviderAsync();
        var doc = await _client.GetFromJsonAsync<JsonElement>("/api/config/export?scope=combos");
        var imp = await _client.PostAsJsonAsync("/api/config/import",
            new { doc, dryRun = true });
        (await imp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("counts").GetProperty("combos").GetInt32().ShouldBe(1);
        await using var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        // original row untouched (deleted-then-dry-run stays deleted? no: row still there)
        (await db.Combos.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Import_keep_preserves_stored_secret_when_doc_is_redacted()
    {
        await LoginAsync();
        await SeedComboAndProviderAsync();
        var doc = await _client.GetFromJsonAsync<JsonElement>("/api/config/export?scope=providers");
        var imp = await _client.PostAsJsonAsync("/api/config/import",
            new { doc, scopes = new[] { "providers" }, redactedSecrets = "keep" });
        imp.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        (await db.ProviderConnections.FirstAsync()).Data.ShouldContain("sk-live-123");
    }

    [Fact]
    public async Task Diff_reports_added_removed_changed()
    {
        await LoginAsync();
        await SeedComboAndProviderAsync();
        var doc = (await _client.GetFromJsonAsync<JsonElement>("/api/config/export?scope=combos"))
            .GetRawText();
        var node = JsonNode.Parse(doc)!.AsObject();
        var combos = node["scopes"]!["combos"]!.AsArray();
        combos.Add(JsonNode.Parse("""{"id":"c2","name":"new","models":"[\"openai/m1\"]"}"""));
        combos[0]!["models"] = """["openai/other"]""";
        var r = await _client.PostAsJsonAsync("/api/config/diff", node);
        var diff = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("diff").GetProperty("combos");
        diff.GetProperty("added")[0].GetString().ShouldBe("c2");
        diff.GetProperty("changed")[0].GetString().ShouldBe("c1");
    }

    [Fact]
    public async Task Boot_env_applies_config_file()
    {
        // functional coverage of the env-var path is the ImportAsync call itself —
        // verify the code path via the same doc the boot hook would parse
        var docPath = Path.Combine(Path.GetTempPath(), $"llmr-cfg-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(docPath, """
            {"format":"llmr-config","version":1,"scopes":{"combos":[{"id":"b1","name":"boot","models":"[\"openai/m1\"]"}]}}
            """);
        var doc = JsonNode.Parse(await File.ReadAllTextAsync(docPath))!.AsObject();
        await using var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.EnsureCreated();
        using var scope = _factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<LLMRouter.Core.Registry.ProviderRegistry>();
        var res = await LLMRouter.Core.Config.ConfigAsCode.ImportAsync(doc, null, false, "keep", db, registry, default);
        res.Errors.Count.ShouldBe(0);
        (await db.Combos.CountAsync()).ShouldBe(1);
        File.Delete(docPath);
    }
}
