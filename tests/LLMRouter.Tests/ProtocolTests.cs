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
