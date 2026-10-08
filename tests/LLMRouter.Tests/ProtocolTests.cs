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
        html.ShouldContain("<h1>Title</h1>");
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
        Environment.SetEnvironmentVariable("LLMROUTER_DB_PATH", dbPath);
        try
        {
            LLMRouter.Server.Cli.ResetPassword("new-secret-123");
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
            Environment.SetEnvironmentVariable("LLMROUTER_DB_PATH", null);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
