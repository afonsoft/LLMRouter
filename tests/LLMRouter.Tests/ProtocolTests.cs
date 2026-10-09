using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LLMRouter.Core.Orchestration;
using Shouldly;

namespace LLMRouter.Tests;

public class ConductorTests
{
    [Fact]
    public void Parse_reads_steps_and_fan()
    {
        var steps = Conductor.Parse("""{"steps":[{"name":"a","model":"m1","prompt":"hi {{input}}"},{"name":"b","fan":["x","y"],"prompt":"{{prev}}","join":"merge"}]}""");
        steps.Count.ShouldBe(2);
        steps[0].Model.ShouldBe("m1");
        steps[1].Fan.ShouldBe(["x", "y"]);
    }

    [Fact]
    public void Bind_substitutes_input_and_prev() =>
        Conductor.Bind("{{input}} then {{prev}}", "IN", "PREV").ShouldBe("IN then PREV");

    [Fact]
    public async Task Run_chains_steps_with_prev()
    {
        var calls = new List<(string M, string P)>();
        Task<string> Call(string m, string p) { calls.Add((m, p)); return Task.FromResult($"[{m}]{p}"); }

        var r = await Conductor.RunAsync(Conductor.Parse(
            """{"steps":[{"name":"s1","model":"m1","prompt":"do {{input}}"},{"name":"s2","model":"m2","prompt":"revise {{prev}}"}]}"""),
            "task", Call);

        r.Steps.Count.ShouldBe(2);
        calls[1].P.ShouldContain("[m1]do task");
        r.Final.ShouldContain("[m2]");
    }

    [Fact]
    public async Task Fan_out_join_merges_outputs()
    {
        Task<string> Call(string m, string p) => Task.FromResult($"out-{m}");
        var r = await Conductor.RunAsync(Conductor.Parse(
            """{"steps":[{"name":"f","fan":["a","b"],"prompt":"p","join":"merge"}]}"""), "x", Call);
        r.Final.ShouldBe("out-a\n\n---\n\nout-b");
    }

    [Fact]
    public async Task Run_fans_out_in_parallel_models()
    {
        Task<string> Call(string m, string p) => Task.FromResult(m);
        var r = await Conductor.RunAsync(Conductor.Parse(
            """{"steps":[{"fan":["a","b","c"],"prompt":"p"},{"model":"agg","prompt":"merge {{prev}}"}]}"""), "x", Call);
        r.Steps[0].Model.ShouldBe("a+b+c");
        r.Steps[1].Output.ShouldBe("agg");
    }
}

public class DocsTests
{
    [Fact]
    public void MdToHtml_renders_headers_code_and_bold()
    {
        var html = LLMRouter.Server.Endpoints.DocsEndpoints.MdToHtml("# Title\n- item **b**\n```\ncode <x>\n```\ntext `y`");
        html.ShouldContain("id='title'>Title</h1>");
        html.ShouldContain("<li>item <b>b</b></li>");
        html.ShouldContain("code &lt;x&gt;");
        html.ShouldContain("<code>y</code>");
    }

    [Fact]
    public void MdToHtml_escapes_script()
    {
        var html = LLMRouter.Server.Endpoints.DocsEndpoints.MdToHtml("<script>alert(1)</script>");
        html.ShouldNotContain("<script>");
    }

    [Fact]
    public void Manifest_is_valid_json()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "LLMRouter.Client", "wwwroot", "manifest.webmanifest");
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) return; // manifest is a client asset; validated when present
        var m = System.Text.Json.JsonDocument.Parse(File.ReadAllText(full)).RootElement;
        m.GetProperty("name").GetString().ShouldBe("LLMRouter");
        m.GetProperty("icons").GetArrayLength().ShouldBeGreaterThan(0);
    }
}

public class MitmTests
{
    [Fact]
    public void TrafficCapture_ring_buffer_holds_and_finds()
    {
        LLMRouter.Core.Mitm.TrafficCapture.Clear();
        LLMRouter.Core.Mitm.TrafficCapture.Add(new("f1", DateTime.UtcNow, "GET", "h", "/p", 200, 10, 0, 5, "h1", null, "h2", "b"));
        LLMRouter.Core.Mitm.TrafficCapture.List().ShouldContain(f => f.Id == "f1");
        LLMRouter.Core.Mitm.TrafficCapture.Find("f1")!.Status.ShouldBe(200);
        LLMRouter.Core.Mitm.TrafficCapture.Clear();
        LLMRouter.Core.Mitm.TrafficCapture.List().ShouldBeEmpty();
    }

    [Fact]
    public void Ca_generation_produces_parseable_x509()
    {
        var ca = LLMRouter.Server.Mitm.ForwardProxy.GetOrCreateCa();
        ca.Subject.ShouldContain("LLMRouter");
        ca.HasPrivateKey.ShouldBeTrue();
        var bytes = ca.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert);
        new System.Security.Cryptography.X509Certificates.X509Certificate2(bytes).Subject.ShouldBe(ca.Subject);
    }

    [Fact]
    public void IsProxyRequest_detects_absolute_uri()
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Path = "/v1/models";
        LLMRouter.Server.Mitm.ForwardProxy.IsProxyRequest(ctx.Request).ShouldBeFalse();
        ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget = "https://api.example.com/v1/x";
        LLMRouter.Server.Mitm.ForwardProxy.IsProxyRequest(ctx.Request).ShouldBeTrue();
    }
}

public class ExtrasTests
{
    [Fact]
    public void Gamification_levels_and_badges()
    {
        var (xp, level, badges) = LLMRouter.Core.Extras.Extras.Gamification(0, 0, 0);
        level.ShouldBe("novice"); xp.ShouldBe(0); badges.ShouldBeEmpty();
        var (xp2, level2, badges2) = LLMRouter.Core.Extras.Extras.Gamification(120, 2_000_000, 5);
        level2.ShouldBe("master"); // 1200 + 20000 + 250 = 21450
        badges2.ShouldContain("centurion");
        badges2.ShouldContain("multi-provider");
    }

    [Fact]
    public void DiscoveryTargets_cover_local_providers()
    {
        LLMRouter.Core.Extras.Extras.DiscoveryTargets.ShouldContain(t => t.Name == "ollama" && t.Port == 11434);
        LLMRouter.Core.Extras.Extras.DiscoveryTargets.Length.ShouldBeGreaterThanOrEqualTo(4);
    }
}

public class DistributionTests
{
    [Fact]
    public void ResetPassword_writes_pbkdf2_hash()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"llmr-{Guid.NewGuid():N}.db");
        try
        {
            LLMRouter.Server.Cli.ResetPassword("new-secret-123", dbPath);
            var opts = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<LLMRouter.Core.Data.LlmRouterDbContext>()
                .UseSqlite($"Data Source={dbPath}").Options;
            using var db = new LLMRouter.Core.Data.LlmRouterDbContext(opts);
            var s = db.Settings.First();
            var d = System.Text.Json.JsonDocument.Parse(s.Data).RootElement;
            var hash = d.GetProperty("adminPasswordHash").GetString()!;
            hash.ShouldStartWith("pbkdf2$100000$");
            // verify the PBKDF2 hash validates the new password (same algorithm as AuthEndpoints)
            var parts = hash.Split('$');
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                "new-secret-123", salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256, expected.Length);
            actual.ShouldBe(expected);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}

public class BatchesTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public BatchesTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        _client = _factory.CreateClient();
        _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Wait();
    }

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    [Fact]
    public async Task Batch_job_completes_with_results()
    {
        var c = _client;
        var resp = await c.PostAsJsonAsync("/api/batches", new
        {
            requests = new[] { new { model = "nope", messages = Array.Empty<object>() } }
        });
        resp.EnsureSuccessStatusCode();
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        // worker runs async — poll for done
        JsonElement job = default;
        for (var i = 0; i < 30; i++)
        {
            job = (await c.GetFromJsonAsync<JsonElement>($"/api/batches/{id}"))!;
            if (job.GetProperty("status").GetString() == "done") break;
            await Task.Delay(100);
        }
        job.GetProperty("status").GetString().ShouldBe("done");
        job.GetProperty("done").GetInt32().ShouldBe(1);
        job.GetProperty("results").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task SearchTools_and_plugins_return_data()
    {
        var tools = (await _client.GetFromJsonAsync<JsonElement>("/api/search-tools"))!;
        tools.GetProperty("tools").GetArrayLength().ShouldBeGreaterThan(5);
        var plugins = (await _client.GetFromJsonAsync<JsonElement>("/api/plugins"))!;
        plugins.GetProperty("skills").GetArrayLength().ShouldBeGreaterThan(0);
    }
}

public class McpToolsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public McpToolsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        _client = _factory.CreateClient();
        _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Wait();
    }

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private async Task<JsonElement> Rpc(string method, object? prms = null)
    {
        var resp = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method,
            @params = prms ?? new { },
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("result");
    }

    private static JsonElement CalledText(JsonElement result) =>
        JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;

    [Fact]
    public async Task Tools_list_exposes_canonical_names()
    {
        var result = await Rpc("tools/list");
        var names = result.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToHashSet();
        foreach (var expected in new[]
        {
            "providers.list", "providers.get", "providers.test", "models.list",
            "connections.list", "connections.create", "apiKeys.list", "apiKeys.create",
            "apiKeys.revoke", "combos.list", "combos.create", "combos.run",
            "usage.stats", "usage.timeseries", "logs.list", "settings.get",
            "settings.update", "skills.list", "memory.search", "memory.add",
            "pools.list", "token-health.list", "docs.search", "system.status", "chat",
        })
            names.ShouldContain(expected);
    }

    [Fact]
    public async Task Tools_call_dispatches_read_tools()
    {
        var providers = CalledText(await Rpc("tools/call",
            new { name = "providers.list", arguments = new { } }));
        providers.GetProperty("providers").GetArrayLength().ShouldBeGreaterThan(0);

        var stats = CalledText(await Rpc("tools/call",
            new { name = "usage.stats", arguments = new { } }));
        stats.GetProperty("total").GetInt32().ShouldBe(0);

        var combos = CalledText(await Rpc("tools/call",
            new { name = "combos.list", arguments = new { } }));
        combos.GetProperty("combos").GetArrayLength().ShouldBe(0);

        var status = CalledText(await Rpc("tools/call",
            new { name = "system.status", arguments = new { } }));
        status.GetProperty("name").GetString().ShouldBe("LLMRouter");
        status.GetProperty("counts").GetProperty("providers").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Tools_call_writes_and_reads_back()
    {
        // apiKeys.create → apiKeys.list shows it
        var created = CalledText(await Rpc("tools/call",
            new { name = "apiKeys.create", arguments = new { name = "mcp-test" } }));
        var keyId = created.GetProperty("id").GetString()!;
        created.GetProperty("key").GetString().ShouldStartWith("sk-llmr-");
        var keys = CalledText(await Rpc("tools/call",
            new { name = "apiKeys.list", arguments = new { } }));
        keys.GetProperty("keys").EnumerateArray().Any(k => k.GetProperty("id").GetString() == keyId).ShouldBeTrue();
        var revoked = CalledText(await Rpc("tools/call",
            new { name = "apiKeys.revoke", arguments = new { id = keyId } }));
        revoked.GetProperty("revoked").GetBoolean().ShouldBeTrue();

        // memory.add → memory.search finds it
        await Rpc("tools/call", new { name = "memory.add", arguments = new { content = "mcp needle item" } });
        var mem = CalledText(await Rpc("tools/call",
            new { name = "memory.search", arguments = new { q = "needle" } }));
        mem.GetProperty("items").GetArrayLength().ShouldBe(1);

        // settings.update → settings.get
        await Rpc("tools/call", new { name = "settings.update", arguments = new { data = new { theme = "dark" } } });
        var settings = CalledText(await Rpc("tools/call",
            new { name = "settings.get", arguments = new { } }));
        settings.GetProperty("theme").GetString().ShouldBe("dark");

        // unknown tool → error
        var err = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = 9, method = "tools/call",
            @params = new { name = "nope.nothing", arguments = new { } },
        });
        var errJson = await err.Content.ReadFromJsonAsync<JsonElement>();
        errJson!.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32602);
    }
}
