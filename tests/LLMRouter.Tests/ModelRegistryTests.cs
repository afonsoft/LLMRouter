using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Routing;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-071: capability overrides CRUD + gateway consumption,
/// synced-available-models, free-provider-rankings.</summary>
public class ModelRegistryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly CannedUpstream _upstream = new();

    public ModelRegistryTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                    c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
                .ConfigureServices(s =>
                    s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => _upstream)));
        _client = _factory.CreateClient();
        _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Wait();
    }

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db()
    {
        var o = new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options;
        var db = new LlmRouterDbContext(o);
        db.Database.EnsureCreated();
        return db;
    }

    private sealed class CannedUpstream : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder = _ =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = Array.Empty<object>() }) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Responder(request));
    }

    // ---- capability overrides CRUD ----

    [Fact]
    public async Task Capability_overrides_crud_cycle()
    {
        var create = await _client.PostAsJsonAsync("/api/model-capability-overrides",
            new { provider = "openai", model = "gpt-4o", capabilities = new { vision = true, tools = true, streaming = false } });
        create.StatusCode.ShouldBe(HttpStatusCode.OK);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        created.GetProperty("created").GetBoolean().ShouldBeTrue();
        var id = created.GetProperty("override").GetProperty("id").GetString()!;
        id.Length.ShouldBeGreaterThan(0);

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/model-capability-overrides");
        var rows = list.GetProperty("overrides").EnumerateArray().ToList();
        rows.Count.ShouldBe(1);
        rows[0].GetProperty("provider").GetString().ShouldBe("openai");
        rows[0].GetProperty("capabilities").GetProperty("vision").GetBoolean().ShouldBeTrue();
        rows[0].GetProperty("capabilities").GetProperty("streaming").GetBoolean().ShouldBeFalse();

        // same provider+model upserts instead of duplicating
        var up = await _client.PostAsJsonAsync("/api/model-capability-overrides",
            new { provider = "openai", model = "gpt-4o", capabilities = new { reasoning = true } });
        var upj = await up.Content.ReadFromJsonAsync<JsonElement>();
        upj.GetProperty("created").GetBoolean().ShouldBeFalse();
        upj.GetProperty("override").GetProperty("id").GetString().ShouldBe(id);

        var filtered = await _client.GetFromJsonAsync<JsonElement>("/api/model-capability-overrides?provider=openai");
        filtered.GetProperty("total").GetInt32().ShouldBe(1);
        var other = await _client.GetFromJsonAsync<JsonElement>("/api/model-capability-overrides?provider=anthropic");
        other.GetProperty("total").GetInt32().ShouldBe(0);

        var put = await _client.PutAsJsonAsync($"/api/model-capability-overrides/{id}",
            new { capabilities = new { streaming = true } });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var putj = await put.Content.ReadFromJsonAsync<JsonElement>();
        putj.GetProperty("override").GetProperty("capabilities").GetProperty("streaming").GetBoolean().ShouldBeTrue();

        var del = await _client.DeleteAsync($"/api/model-capability-overrides/{id}");
        del.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetFromJsonAsync<JsonElement>("/api/model-capability-overrides"))
            .GetProperty("total").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Capability_overrides_validation_and_404_return_json()
    {
        var noModel = await _client.PostAsJsonAsync("/api/model-capability-overrides",
            new { provider = "openai", capabilities = new { vision = true } });
        noModel.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await noModel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .ShouldContain("required");

        var badCaps = await _client.PostAsJsonAsync("/api/model-capability-overrides",
            new { provider = "openai", model = "m", capabilities = new[] { "vision" } });
        badCaps.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await badCaps.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!
            .ShouldContain("capabilities");

        var put404 = await _client.PutAsJsonAsync("/api/model-capability-overrides/nope",
            new { capabilities = new { vision = true } });
        put404.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await put404.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .ShouldBe("not found");

        var del404 = await _client.DeleteAsync("/api/model-capability-overrides/nope");
        del404.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await del404.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .ShouldBe("not found");
    }

    // ---- gateway consumption ----

    [Fact]
    public async Task Override_reorders_combo_models_in_engine()
    {
        await using (var db = Db())
        {
            db.ProviderConnections.AddRange(
                new ProviderConnection { Id = "c-bai", Provider = "bai", AuthType = "apikey", IsActive = true },
                new ProviderConnection { Id = "c-dgrid", Provider = "dgrid", AuthType = "apikey", IsActive = true });
            db.Combos.Add(new Combo
            {
                Id = "cb-cap", Name = "cap-combo", Kind = "fallback",
                Models = "[\"bai/m-a\",\"dgrid/m-b\"]",
                CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01",
            });
            db.CapabilityOverrides.Add(new CapabilityOverride
            {
                Id = "ov1", Provider = "dgrid", Model = "m-b",
                Capabilities = "{\"vision\":true}", UpdatedAt = "2026-01-01",
            });
            await db.SaveChangesAsync();
        }

        var body = JsonSerializer.Deserialize<JsonElement>(
            """{"messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/png;base64,AA"}}]}]}""");
        using var scope = _factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<GatewayEngine>();
        var targets = await engine.ResolveAsync("cap-combo", body);
        targets.Count.ShouldBe(2);
        // dgrid/m-b got vision via the override row → promoted ahead of bai/m-a
        targets[0].Provider.Id.ShouldBe("dgrid");
        targets[0].UpstreamModel.ShouldBe("m-b");
    }

    [Fact]
    public async Task LoadOverrides_parses_object_and_array_forms()
    {
        await using (var db = Db())
        {
            db.CapabilityOverrides.AddRange(
                new CapabilityOverride { Id = "o1", Provider = "p1", Model = "m1",
                    Capabilities = "{\"vision\":true,\"tools\":false}", UpdatedAt = "x" },
                new CapabilityOverride { Id = "o2", Provider = "p2", Model = "m2",
                    Capabilities = "[\"streaming\",\"pdf\"]", UpdatedAt = "x" });
            await db.SaveChangesAsync();
        }
        await using (var db = Db())
        {
            var map = await ComboPlanner.LoadOverridesAsync(db);
            map["p1/m1"].ShouldContain("vision");
            map["p1/m1"].ShouldNotContain("tools");
            map["p2/m2"].ShouldContain("streaming");
            map["p2/m2"].ShouldContain("pdf");
        }
    }

    [Fact]
    public void Reorder_honors_overrides_map()
    {
        var planner = new ComboPlanner(new LLMRouter.Core.Registry.ProviderRegistry());
        var models = new List<string> { "b/m2", "a/m1" };
        var noOverride = planner.ReorderByCapabilities(models, ["vision"]);
        noOverride.ShouldBe(models); // unknown providers → no caps → stable

        var overrides = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["a/m1"] = ["vision"],
        };
        var reordered = planner.ReorderByCapabilities(models, ["vision"], overrides);
        reordered[0].ShouldBe("a/m1");
        reordered.Count.ShouldBe(2); // never drops
    }

    // ---- synced available models ----

    private async Task SeedConnectionAsync(string id, string provider)
    {
        await using var db = Db();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = id, Provider = provider, AuthType = "apikey", IsActive = true,
            Data = """{"apiKey":"sk-test"}""",
            CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sync_populates_and_stale_marks_unavailable()
    {
        await SeedConnectionAsync("c-bai", "bai");
        _upstream.Responder = _ => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { data = new[] { new { id = "m-a" }, new { id = "m-b" } } }),
        };

        var sync = await _client.PostAsJsonAsync("/api/synced-available-models", new { provider = "bai" });
        sync.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sj = await sync.Content.ReadFromJsonAsync<JsonElement>();
        sj.GetProperty("synced").GetInt32().ShouldBe(1);
        sj.GetProperty("results")[0].GetProperty("ok").GetBoolean().ShouldBeTrue();
        sj.GetProperty("results")[0].GetProperty("count").GetInt32().ShouldBe(2);

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/synced-available-models");
        list.GetProperty("total").GetInt32().ShouldBe(2);
        var models = list.GetProperty("models").EnumerateArray()
            .Select(m => m.GetProperty("model").GetString()).ToList();
        models.ShouldBe(["m-a", "m-b"]);

        // next sync drops m-b upstream → row kept but flagged unavailable
        _upstream.Responder = _ => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { data = new[] { new { id = "m-a" }, new { id = "m-c" } } }),
        };
        var sync2 = await _client.PostAsJsonAsync("/api/synced-available-models", new { provider = "bai" });
        sync2.StatusCode.ShouldBe(HttpStatusCode.OK);

        var avail = await _client.GetFromJsonAsync<JsonElement>("/api/synced-available-models?available=true");
        avail.GetProperty("total").GetInt32().ShouldBe(2); // m-a, m-c
        var gone = await _client.GetFromJsonAsync<JsonElement>("/api/synced-available-models?available=false");
        gone.GetProperty("total").GetInt32().ShouldBe(1);
        gone.GetProperty("models")[0].GetProperty("model").GetString().ShouldBe("m-b");
    }

    [Fact]
    public async Task Sync_unknown_provider_returns_json_error()
    {
        var r = await _client.PostAsJsonAsync("/api/synced-available-models", new { provider = "nope" });
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!
            .ShouldContain("nope");
    }

    [Fact]
    public async Task Sync_provider_without_models_endpoint_reports_error()
    {
        await SeedConnectionAsync("c-openai", "openai"); // registry entry has no modelsUrl
        var r = await _client.PostAsJsonAsync("/api/synced-available-models", new { provider = "openai" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        j.GetProperty("synced").GetInt32().ShouldBe(0);
        j.GetProperty("results")[0].GetProperty("ok").GetBoolean().ShouldBeFalse();
        j.GetProperty("results")[0].GetProperty("error").GetString().ShouldBe("no models endpoint");
    }

    // ---- free provider rankings ----

    [Fact]
    public async Task Free_provider_rankings_rank_by_availability_and_latency()
    {
        var now = DateTime.UtcNow.ToString("O");
        await using (var db = Db())
        {
            db.ProviderConnections.AddRange(
                new ProviderConnection { Id = "r-ok1", Provider = "ollama", AuthType = "none", IsActive = true },
                new ProviderConnection { Id = "r-ok2", Provider = "ollama", AuthType = "none", IsActive = true },
                new ProviderConnection { Id = "r-bad", Provider = "lmstudio", AuthType = "none", IsActive = false },
                new ProviderConnection { Id = "r-paid", Provider = "openai", AuthType = "apikey", IsActive = true });
            for (var i = 0; i < 4; i++)
                db.UsageHistory.Add(new UsageRecord
                {
                    Timestamp = now, Provider = "lmstudio", Model = "m", ConnectionId = "r-bad",
                    LatencyMs = 9000, Status = "error",
                });
            db.UsageHistory.Add(new UsageRecord
            {
                Timestamp = now, Provider = "ollama", Model = "m", ConnectionId = "r-ok1",
                LatencyMs = 100, Status = "ok",
            });
            await db.SaveChangesAsync();
        }

        var j = await _client.GetFromJsonAsync<JsonElement>("/api/free-provider-rankings");
        var providers = j.GetProperty("providers").EnumerateArray().ToList();
        providers.Count.ShouldBe(2); // ollama + lmstudio — openai conn is not free-flagged
        providers[0].GetProperty("provider").GetString().ShouldBe("ollama");
        providers[0].GetProperty("rank").GetInt32().ShouldBe(1);
        providers[0].GetProperty("availability").GetDouble().ShouldBe(100);
        providers[1].GetProperty("provider").GetString().ShouldBe("lmstudio");
        providers[1].GetProperty("availability").GetDouble().ShouldBe(0);
        providers[1].GetProperty("errorRate").GetDouble().ShouldBe(1);
        providers[0].GetProperty("score").GetDouble()
            .ShouldBeGreaterThan(providers[1].GetProperty("score").GetDouble());
        j.GetProperty("total").GetInt32().ShouldBe(2);
        j.GetProperty("healthy").GetInt32().ShouldBe(1);
    }
}
