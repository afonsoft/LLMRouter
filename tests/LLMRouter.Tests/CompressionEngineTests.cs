using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Compression;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-034: compression engines and pipeline.</summary>
public class CompressionEngineTests
{
    private static JsonObject Body(params (string role, string content)[] msgs)
    {
        var arr = new JsonArray();
        foreach (var (r, c) in msgs)
            arr.Add(new JsonObject { ["role"] = r, ["content"] = c });
        return new JsonObject { ["model"] = "m", ["messages"] = arr };
    }

    private static string Texts(JsonObject body) =>
        string.Join("\n", TextOps.Messages(body)
            .Select(m => TextOps.ExtractText(m as JsonObject)));

    [Fact]
    public void Catalog_contains_all_engines_with_stable_flags()
    {
        var ids = CompressionRegistry.Engines.Select(e => e.Id).ToArray();
        foreach (var id in new[] { "lite", "session-dedup", "ccr", "headroom", "caveman", "aggressive", "ultra", "rtk", "llmlingua", "omniglyph" })
            ids.ShouldContain(id);
        CompressionRegistry.Get("llmlingua")!.Stable.ShouldBeFalse();
        CompressionRegistry.Get("omniglyph")!.Stable.ShouldBeFalse();
        CompressionRegistry.Get("lite")!.Stable.ShouldBeTrue();
        CompressionRegistry.Get("caveman")!.ConfigSchema.ShouldNotBeEmpty();
    }

    [Fact]
    public void Lite_collapses_whitespace_and_truncates_tools()
    {
        var body = Body(("system", "You are helpful."),
            ("tool", new string('x', 5000)),
            ("user", "hello   world\n\n\n\nsecond   paragraph"));
        var r = new LiteEngine().Apply(body, new EngineOptions());
        Texts(r.Body).ShouldContain("hello world");
        Texts(r.Body).ShouldContain("[truncated]");
        r.Stats!.SavedChars.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Lite_never_truncates_tool_results_in_current_turn()
    {
        var big = new string('x', 6000);
        var body = Body(("assistant", "a"), ("user", "q"), ("tool", big));
        var r = new LiteEngine().Apply(body, new EngineOptions());
        Texts(r.Body).ShouldContain(big); // last message = current turn → untouched
    }

    [Fact]
    public void SessionDedup_replaces_repeated_blocks_with_markers()
    {
        var block = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i} of a repeated block of text"));
        var body = Body(
            ("system", "sys"),
            ("user", $"prefix A\n\n{block}"),
            ("assistant", "reply"),
            ("user", $"prefix B\n\n{block}"),
            ("assistant", "more"),
            ("user", "current turn"));
        var r = new SessionDedupEngine().Apply(body, new EngineOptions());
        Texts(r.Body).ShouldContain("[dedup:");
        r.Stats!.SavedChars.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Ccr_compress_then_retrieve_roundtrip()
    {
        CcrStore.Clear();
        var big = new string('y', 2000);
        var body = Body(("user", $"start\n\n{big}\n\nend"), ("assistant", "a"), ("user", "now"));
        var r = new CcrEngine().Apply(body, new EngineOptions(PrincipalId: "p1"));
        Texts(r.Body).ShouldContain("[CCR retrieve hash=");

        var hash = System.Text.RegularExpressions.Regex.Match(Texts(r.Body), @"hash=([0-9a-f]{24})").Groups[1].Value;
        hash.Length.ShouldBe(24);
        var block = CcrStore.Get(hash, "p1");
        block.ShouldBe(big);
    }

    [Fact]
    public void Ccr_skips_when_above_retrieval_threshold()
    {
        CcrStore.Clear();
        var big = new string('z', 2000);
        var body = Body(("user", $"start\n\n{big}\n\nend"), ("assistant", "a"), ("user", "now"));
        var r1 = new CcrEngine().Apply(body, new EngineOptions(PrincipalId: "p2"));
        var hash = System.Text.RegularExpressions.Regex.Match(Texts(r1.Body), @"hash=([0-9a-f]{24})").Groups[1].Value;
        for (var i = 0; i < 3; i++) CcrStore.RecordRetrieval(hash, "p2");
        var r2 = new CcrEngine().Apply(Body(("user", $"start\n\n{big}\n\nend"), ("assistant", "a"), ("user", "now")),
            new EngineOptions(PrincipalId: "p2"));
        Texts(r2.Body).ShouldNotContain("[CCR retrieve");
    }

    [Fact]
    public void Caveman_compresses_and_preserves_code()
    {
        var body = Body(("user",
            "In order to configure this you will need to do the following. " +
            "I am not sure if this is going to work or not. " +
            "For example:\n```csharp\nvar x = in order to test;\n```"));
        var r = new CavemanEngine().Apply(body, new EngineOptions(
            StepConfig: new JsonObject { ["intensity"] = "full" }));
        var t = Texts(r.Body);
        t.ShouldContain("To configure");
        t.ShouldContain("in order to test"); // inside fenced code — preserved
        t.ShouldNotContain("I am not sure if");
        r.Stats!.SavedChars.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Headroom_encodes_json_arrays_as_omni_tabular()
    {
        var rows = Enumerable.Range(0, 12).Select(i => $"{{\"name\":\"n{i}\",\"port\":{8000 + i}}}");
        var body = Body(("user", $"run output:\n[{string.Join(",", rows)}]\nmore"), ("assistant", "ok"), ("user", "next"));
        var r = new HeadroomEngine().Apply(body, new EngineOptions());
        Texts(r.Body).ShouldContain("```omni-tabular");
        Texts(r.Body).ShouldContain("cols:name,port");
        Texts(r.Body).ShouldContain("\"n0\"|8000");
    }

    [Fact]
    public void Aggressive_summarizes_aged_turns()
    {
        var msgs = new List<(string, string)> { ("system", "sys") };
        for (var i = 0; i < 8; i++)
        {
            msgs.Add(("user", $"question {i} " + new string('q', 1500)));
            msgs.Add(("assistant", $"answer {i} " + new string('a', 1500)));
        }
        msgs.Add(("user", "current turn"));
        var body = Body(msgs.ToArray());
        var r = new AggressiveEngine().Apply(body, new EngineOptions());
        Texts(r.Body).ShouldContain("[COMPRESSED:");
        r.Stats!.SavedChars.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Ultra_prunes_by_score()
    {
        var filler = string.Join(" ", Enumerable.Repeat("the and of a to in it is", 200));
        var body = Body(("user", filler), ("assistant", "a"), ("user", "now"));
        var r = new UltraEngine().Apply(body, new EngineOptions());
        r.Stats!.SavedChars.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Pipeline_runs_steps_in_stack_priority_order()
    {
        var block = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"dup line {i} padding"));
        var body = Body(
            ("user", $"a\n\n{block}"),
            ("assistant", "r"),
            ("user", $"b\n\n{block}"),
            ("assistant", "r2"),
            ("user", "tail   with    spaces\n\n\n\nend"));
        var (outBody, runs) = CompressionPipeline.Run(body,
            [new PipelineStep("session-dedup", null, null), new PipelineStep("lite", null, null)],
            new EngineOptions());
        runs.Count.ShouldBe(2);
        runs[0].Engine.ShouldBe("session-dedup"); // priority 3 before lite 25
        runs[1].Engine.ShouldBe("lite");
    }

    [Fact]
    public void Plan_resolution_prefers_combo_pipeline_then_stacked_then_mode()
    {
        var comboPipeline = JsonNode.Parse("""[{"engine":"ultra"}]""");
        var settings = JsonNode.Parse("""{"defaultMode":"lite","stackedPipeline":[{"engine":"caveman"}]}""") as JsonObject;

        CompressionPipeline.ResolvePlan(settings, "rc1", comboPipeline)
            .Single().Engine.ShouldBe("ultra");
        CompressionPipeline.ResolvePlan(settings, "rc1", null)
            .Single().Engine.ShouldBe("caveman");
        CompressionPipeline.ResolvePlan(JsonNode.Parse("""{"defaultMode":"lite"}""") as JsonObject, "rc1", null)
            .Single().Engine.ShouldBe("lite");
        CompressionPipeline.ResolvePlan(JsonNode.Parse("""{"defaultMode":"off"}""") as JsonObject, "rc1", null)
            .ShouldBeEmpty();
        CompressionPipeline.ResolvePlan(null, "rc1", null).ShouldBeEmpty();
    }

    [Fact]
    public void Preservation_roundtrip_restores_placeholders()
    {
        var text = "Run `dotnet test` and curl https://example.com/x then:\n```\ncode block\n```";
        var (prepped, map) = Preservation.Extract(text, null);
        prepped.ShouldNotContain("dotnet test");
        Preservation.Restore(prepped, map).ShouldBe(text);
    }

    [Fact]
    public void Failing_step_config_is_tolerated_not_thrown()
    {
        var body = Body(("user", "x"));
        var (outBody, _) = CompressionPipeline.Run(body,
            [new PipelineStep("lite", null, null)], new EngineOptions());
        TextOps.BodyTextChars(outBody).ShouldBeGreaterThan(0);
    }
}
