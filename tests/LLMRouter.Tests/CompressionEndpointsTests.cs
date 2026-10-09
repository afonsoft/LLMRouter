using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-034: /api/compression/*, /api/context/*, /api/settings/compression* endpoints.</summary>
public class CompressionEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public CompressionEndpointsTests()
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

    private async Task LoginAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Engine_catalog_lists_all_engines_with_schema()
    {
        await LoginAsync();
        var cat = await _client.GetFromJsonAsync<JsonElement>("/api/compression/engines");
        var ids = cat.GetProperty("engines").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()).ToArray();
        foreach (var id in new[] { "lite", "caveman", "aggressive", "ultra", "session-dedup", "ccr", "headroom", "rtk", "llmlingua", "omniglyph" })
            ids.ShouldContain(id);
        var caveman = cat.GetProperty("engines").EnumerateArray().First(e => e.GetProperty("id").GetString() == "caveman");
        caveman.GetProperty("configSchema").EnumerateArray()
            .Select(f => f.GetProperty("key").GetString()).ShouldContain("intensity");
    }

    [Fact]
    public async Task Rules_and_language_packs_endpoints()
    {
        await LoginAsync();
        var rules = await _client.GetFromJsonAsync<JsonElement>("/api/compression/rules");
        rules.GetProperty("rules").GetArrayLength().ShouldBeGreaterThan(30);
        var packs = await _client.GetFromJsonAsync<JsonElement>("/api/compression/language-packs");
        packs.GetProperty("packs").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ShouldContain("en");
    }

    [Fact]
    public async Task Preview_runs_engine_and_reports_savings()
    {
        await LoginAsync();
        var res = await _client.PostAsJsonAsync("/api/compression/preview", new
        {
            engineId = "lite",
            messages = new[] { new { role = "tool", content = new string('y', 3000) }, new { role = "assistant", content = "ok" }, new { role = "user", content = "now" } },
        });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("engineBreakdown").GetArrayLength().ShouldBe(1);
        body.GetProperty("engineBreakdown")[0].GetProperty("engine").GetString().ShouldBe("lite");
        body.TryGetProperty("stats", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Preview_by_mode_and_compare_all()
    {
        await LoginAsync();
        var res = await _client.PostAsJsonAsync("/api/compression/preview", new
        {
            mode = "caveman",
            text = "In order to do this you will need to for example run the thing at this point in time.",
        });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);

        var cmp = await _client.PostAsJsonAsync("/api/compression/compare", new { text = "x " + new string('a', 3000) });
        var rows = (await cmp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows");
        rows.GetArrayLength().ShouldBeGreaterThan(5);
    }

    [Fact]
    public async Task Ccr_retrieve_roundtrip_and_modes()
    {
        await LoginAsync();
        var big = new string('z', 2000);
        var res = await _client.PostAsJsonAsync("/api/compression/preview", new
        {
            engineId = "ccr",
            principalId = "testp",
            messages = new[] { new { role = "user", content = $"s\n\n{big}\n\ne" }, new { role = "assistant", content = "a" }, new { role = "user", content = "n" } },
        });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var marker = body.GetProperty("body").GetProperty("messages")[0].GetProperty("content").GetString()!;
        var hash = System.Text.RegularExpressions.Regex.Match(marker, @"hash=([0-9a-f]{24})").Groups[1].Value;
        hash.Length.ShouldBe(24);

        var r1 = await _client.PostAsJsonAsync("/api/compression/retrieve", new { hash, principalId = "testp" });
        var rb = await r1.Content.ReadFromJsonAsync<JsonElement>();
        rb.GetProperty("found").GetBoolean().ShouldBeTrue();
        rb.GetProperty("block").GetString().ShouldBe(big);

        var stats = await _client.PostAsJsonAsync("/api/compression/retrieve", new { hash, principalId = "testp", mode = "stats" });
        var sb = await stats.Content.ReadFromJsonAsync<JsonElement>();
        sb.GetProperty("block").GetProperty("chars").GetInt32().ShouldBe(2000);

        var nf = await _client.PostAsJsonAsync("/api/compression/retrieve", new { hash = "000000000000000000000000" });
        (await nf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("found").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Compression_settings_roundtrip()
    {
        await LoginAsync();
        var put = await _client.PutAsJsonAsync("/api/settings/compression", new { defaultMode = "lite", stackedPipeline = new[] { new { engine = "lite" } } });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        var get = await _client.GetFromJsonAsync<JsonElement>("/api/settings/compression");
        get.GetProperty("compression").GetProperty("defaultMode").GetString().ShouldBe("lite");
        get.GetProperty("compression").GetProperty("stackedPipeline").GetArrayLength().ShouldBe(1);

        // second PUT merges, doesn't clobber
        await _client.PutAsJsonAsync("/api/settings/compression", new { defaultMode = "off" });
        var get2 = await _client.GetFromJsonAsync<JsonElement>("/api/settings/compression");
        get2.GetProperty("compression").GetProperty("defaultMode").GetString().ShouldBe("off");
        get2.GetProperty("compression").GetProperty("stackedPipeline").GetArrayLength().ShouldBe(1);

        // generic /api/settings must carry the compression key too
        var all = await _client.GetFromJsonAsync<JsonElement>("/api/settings");
        all.GetProperty("data").TryGetProperty("compression", out var c).ShouldBeTrue();
        c.GetProperty("defaultMode").GetString().ShouldBe("off");
    }

    [Fact]
    public async Task Mcp_accessibility_settings()
    {
        await LoginAsync();
        var put = await _client.PutAsJsonAsync("/api/settings/compression/mcp-accessibility", new { enabled = true, tools = new[] { "files" } });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var get = await _client.GetFromJsonAsync<JsonElement>("/api/settings/compression/mcp-accessibility");
        get.GetProperty("enabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Combos_crud_and_assignments()
    {
        await LoginAsync();
        var create = await _client.PostAsJsonAsync("/api/context/combos", new
        {
            name = "Test combo",
            description = "d",
            pipeline = new[]
            {
                new Dictionary<string, string> { ["engine"] = "session-dedup" },
                new Dictionary<string, string> { ["engine"] = "caveman", ["intensity"] = "full" },
            },
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var combo = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = combo.GetProperty("id").GetString()!;
        combo.GetProperty("pipeline").GetArrayLength().ShouldBe(2);

        var bad = await _client.PostAsJsonAsync("/api/context/combos", new { name = "" });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // assignment requires an existing routing combo — make one
        var rc = await _client.PostAsJsonAsync("/api/combos", new { name = "rc1", models = new[] { "m" } });
        var rcId = (await rc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("combo").GetProperty("id").GetString()!;
        var putA = await _client.PutAsJsonAsync($"/api/context/combos/{id}/assignments", new { routingComboIds = new[] { rcId } });
        putA.StatusCode.ShouldBe(HttpStatusCode.OK);

        var getA = await _client.GetFromJsonAsync<JsonElement>($"/api/context/combos/{id}/assignments");
        getA.GetProperty("assignments")[0].GetProperty("routingComboId").GetString().ShouldBe(rcId);

        // unknown engine in pipeline → 400
        var upd = await _client.PutAsJsonAsync($"/api/context/combos/{id}",
            new { pipeline = new[] { new Dictionary<string, string> { ["engine"] = "bogus-engine" } } });
        upd.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var del = await _client.DeleteAsync($"/api/context/combos/{id}");
        del.StatusCode.ShouldBe(HttpStatusCode.OK);
        var get404 = await _client.GetAsync($"/api/context/combos/{id}");
        get404.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Caveman_config_roundtrip()
    {
        await LoginAsync();
        var put = await _client.PutAsJsonAsync("/api/context/caveman/config",
            new { config = new { enabled = true, intensity = "lite", compressRoles = new[] { "user" } } });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var get = await _client.GetFromJsonAsync<JsonElement>("/api/context/caveman/config");
        get.GetProperty("config").GetProperty("intensity").GetString().ShouldBe("lite");
    }

    [Fact]
    public async Task Analytics_endpoints_return_aggregates()
    {
        await LoginAsync();
        var a = await _client.GetFromJsonAsync<JsonElement>("/api/context/analytics?since=7d");
        a.GetProperty("days").GetInt32().ShouldBe(7);
        a.TryGetProperty("byEngine", out _).ShouldBeTrue();
        var e = await _client.GetFromJsonAsync<JsonElement>("/api/context/analytics/engine?engineId=caveman&days=30");
        e.GetProperty("engineId").GetString().ShouldBe("caveman");
        var miss = await _client.GetAsync("/api/context/analytics/engine");
        miss.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var t = await _client.GetFromJsonAsync<JsonElement>("/api/settings/compression/run-telemetry");
        t.TryGetProperty("totalRuns", out _).ShouldBeTrue();
    }
}
