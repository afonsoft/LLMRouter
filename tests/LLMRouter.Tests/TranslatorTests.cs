using System.Text.Json;
using LLMRouter.Core.Translation;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

public class TranslatorTests
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void OpenAi_to_claude_request()
    {
        var r = Translators.Translate(
            Body("""{"model":"x","max_tokens":50,"messages":[{"role":"system","content":"sys"},{"role":"user","content":"hi"}]}"""),
            "openai", "claude", "claude-x", false);
        r["system"]!.AsArray()[0]!.GetValue<string>().ShouldBe("sys");
        r["messages"]!.AsArray().Count.ShouldBe(1);
        r["messages"]![0]!["role"]!.GetValue<string>().ShouldBe("user");
        r["max_tokens"]!.GetValue<int>().ShouldBe(50);
        r["model"]!.GetValue<string>().ShouldBe("claude-x");
    }

    [Fact]
    public void Claude_to_openai_response()
    {
        var upstream = Body("""{"id":"msg_1","content":[{"type":"text","text":"hello"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":5}}""");
        var r = Translators.TranslateResponse(upstream, "claude", "openai", "m");
        r["choices"]![0]!["message"]!["content"]!.GetValue<string>().ShouldBe("hello");
        r["choices"]![0]!["finish_reason"]!.GetValue<string>().ShouldBe("stop");
        r["usage"]!["prompt_tokens"]!.GetValue<int>().ShouldBe(3);
        r["usage"]!["completion_tokens"]!.GetValue<int>().ShouldBe(5);
    }

    [Fact]
    public void OpenAi_to_gemini_request()
    {
        var r = Translators.Translate(
            Body("""{"model":"x","messages":[{"role":"user","content":"hi"}],"max_tokens":7}"""),
            "openai", "gemini", "gemini-x", false);
        r["contents"]!.AsArray()[0]!["role"]!.GetValue<string>().ShouldBe("user");
        r["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>().ShouldBe("hi");
        r["generationConfig"]!["maxOutputTokens"]!.GetValue<int>().ShouldBe(7);
    }

    [Fact]
    public void Claude_sse_to_openai_delta()
    {
        var state = new Translators.SseState();
        var events = Translators.TranslateSse(
            """{"type":"content_block_delta","delta":{"type":"text_delta","text":"world"}}""",
            "claude", "openai", "m", state).ToList();
        events.Count.ShouldBe(1);
        events[0].Data.ShouldContain("world");
        events[0].Data.ShouldContain("chat.completion.chunk");
    }

    [Fact]
    public void Openai_sse_to_claude_emits_lifecycle()
    {
        var state = new Translators.SseState();
        var events = Translators.TranslateSse(
            """{"choices":[{"delta":{"content":"hi"},"finish_reason":null}]}""",
            "openai", "claude", "m", state).ToList();
        events.Select(e => e.Event).ShouldContain("message_start");
        events.Select(e => e.Event).ShouldContain("content_block_delta");
    }

    [Fact]
    public void Passthrough_same_format()
    {
        var events = Translators.TranslateSse("{\"a\":1}", "openai", "openai", "m", new Translators.SseState()).ToList();
        events.Count.ShouldBe(1);
    }
}
