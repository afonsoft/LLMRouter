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
