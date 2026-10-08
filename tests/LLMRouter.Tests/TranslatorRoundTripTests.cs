using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Translation;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>
/// SPEC-003 round-trip matrix: inbound × outbound format translation for
/// text chat and tool calls, non-streaming and SSE.
/// </summary>
public class TranslatorRoundTripTests
{
    public static IEnumerable<object[]> FormatPairs()
    {
        var formats = new[] { "openai", "claude", "gemini", "responsesApi" };
        foreach (var i in formats)
            foreach (var o in formats)
                yield return new object[] { i, o };
    }

    private static JsonElement BodyFor(string format, bool withTool = false)
    {
        string json = format switch
        {
            "claude" => """{"model":"m","max_tokens":512,"system":"Be helpful","messages":[{"role":"user","content":"Hello"}]}""",
            "gemini" => """{"model":"m","contents":[{"role":"user","parts":[{"text":"Hello"}]}],"generationConfig":{"maxOutputTokens":512}}""",
            "responsesApi" => """{"model":"m","instructions":"Be helpful","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"Hello"}]}]}""",
            _ => withTool
                ? """{"model":"m","max_tokens":512,"messages":[{"role":"system","content":"Be helpful"},{"role":"user","content":"What's the weather?"}],"tools":[{"type":"function","function":{"name":"get_weather","description":"Get weather","parameters":{"type":"object","properties":{"city":{"type":"string"}}}}}]}"""
                : """{"model":"m","max_tokens":512,"messages":[{"role":"system","content":"Be helpful"},{"role":"user","content":"Hello"}]}""",
        };
        return JsonDocument.Parse(json).RootElement;
    }

    [Theory]
    [MemberData(nameof(FormatPairs))]
    public void Request_translation_produces_valid_outbound_shape(string inbound, string outbound)
    {
        var result = Translators.Translate(BodyFor(inbound), inbound, outbound, "target-model", false)
            .AsObject();

        result["model"]!.GetValue<string>().ShouldBe("target-model");
        switch (outbound)
        {
            case "claude":
                result.ShouldContainKey("messages");
                result.ShouldContainKey("max_tokens");
                result["messages"]!.AsArray().Count.ShouldBeGreaterThan(0);
                result["messages"]!.AsArray().Last()!["role"]!.GetValue<string>().ShouldBe("user");
                break;
            case "gemini":
                result.ShouldContainKey("contents");
                break;
            case "responsesApi":
                result.ShouldContainKey("input");
                break;
            default:
                result.ShouldContainKey("messages");
                break;
        }
    }

    [Fact]
    public void Openai_tools_translate_to_claude_tools()
    {
        var result = Translators.Translate(BodyFor("openai", withTool: true), "openai", "claude", "m", false)
            .AsObject();
        var tools = result["tools"]!.AsArray();
        tools.Count.ShouldBe(1);
        tools[0]!["name"]!.GetValue<string>().ShouldBe("get_weather");
        tools[0]!["input_schema"].ShouldNotBeNull();
    }

    [Fact]
    public void Claude_tool_use_and_result_translate_to_openai()
    {
        var claude = JsonDocument.Parse("""
            {"model":"m","max_tokens":100,"messages":[
              {"role":"user","content":"weather?"},
              {"role":"assistant","content":[{"type":"tool_use","id":"tu_1","name":"get_weather","input":{"city":"SP"}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_1","content":"sunny"}]}
            ],"tools":[{"name":"get_weather","description":"w","input_schema":{"type":"object"}}]}
            """).RootElement;
        var result = Translators.Translate(claude, "claude", "openai", "m", false).AsObject();
        var msgs = result["messages"]!.AsArray();
        // assistant with tool_calls + tool result message present
        msgs.Any(m => m!["tool_calls"] != null).ShouldBeTrue();
        msgs.Any(m => m!["role"]!.GetValue<string>() == "tool").ShouldBeTrue();
        result["tools"]!.AsArray()[0]!["function"]!["name"]!.GetValue<string>().ShouldBe("get_weather");
    }

    [Fact]
    public void Responses_inbound_normalizes_to_messages_then_translates()
    {
        var result = Translators.Translate(BodyFor("responsesApi"), "responsesApi", "claude", "m", false)
            .AsObject();
        result.ShouldContainKey("system");
        result["messages"]!.AsArray().ShouldContain(m =>
            m!["role"]!.GetValue<string>() == "user");
    }

    [Fact]
    public void Outbound_responses_produces_input_array()
    {
        var result = Translators.Translate(BodyFor("openai"), "openai", "responsesApi", "m", false)
            .AsObject();
        result.ShouldContainKey("input");
        result["instructions"]!.GetValue<string>().ShouldContain("helpful");
    }

    [Theory]
    [InlineData("claude", "openai")]
    [InlineData("gemini", "openai")]
    [InlineData("openai", "claude")]
    [InlineData("responsesApi", "openai")]
    [InlineData("openai", "responsesApi")]
    public void Response_translation_produces_inbound_shape(string outbound, string inbound)
    {
        var upstream = outbound switch
        {
            "claude" => JsonDocument.Parse("""{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"Hi"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":2}}""").RootElement,
            "gemini" => JsonDocument.Parse("""{"candidates":[{"content":{"role":"model","parts":[{"text":"Hi"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":2,"totalTokenCount":5}}""").RootElement,
            "responsesApi" => JsonDocument.Parse("""{"id":"resp_1","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Hi"}]}],"usage":{"input_tokens":3,"output_tokens":2}}""").RootElement,
            _ => JsonDocument.Parse("""{"id":"chatcmpl-1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"Hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""").RootElement,
        };
        var result = Translators.TranslateResponse(upstream, outbound, inbound, "m").AsObject();
        switch (inbound)
        {
            case "claude":
                result["type"]!.GetValue<string>().ShouldBe("message");
                result["content"]!.AsArray()[0]!["text"]!.GetValue<string>().ShouldBe("Hi");
                break;
            case "responsesApi":
                result["object"]!.GetValue<string>().ShouldBe("response");
                result["output_text"]!.GetValue<string>().ShouldBe("Hi");
                break;
            default: // openai + gemini get the openai-shaped chunk then re-shaped
                result["object"]!.GetValue<string>().ShouldBe("chat.completion");
                result["choices"]!.AsArray()[0]!["message"]!["content"]!.GetValue<string>().ShouldBe("Hi");
                break;
        }
    }

    [Fact]
    public void Claude_sse_tool_use_streams_as_openai_tool_calls()
    {
        var state = new Translators.SseState();
        var start = """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"tu_1","name":"get_weather"}}""";
        var delta = """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"city\":"}}""";
        var evs1 = Translators.TranslateSse(start, "claude", "openai", "m", state).ToList();
        evs1.ShouldContain(e => e.Data.Contains("\"tool_calls\"") && e.Data.Contains("get_weather"));
        var evs2 = Translators.TranslateSse(delta, "claude", "openai", "m", state).ToList();
        evs2.ShouldContain(e => e.Data.Contains("\"arguments\":\"{\\\"city\\\":\""));
    }

    [Fact]
    public void Openai_sse_tool_calls_stream_as_claude_tool_use_blocks()
    {
        var state = new Translators.SseState();
        var start = """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}""";
        var delta = """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":\"SP\"}"}}]},"finish_reason":null}]}""";
        var evs1 = Translators.TranslateSse(start, "openai", "claude", "m", state).ToList();
        evs1.ShouldContain(e => e.Data.Contains("tool_use") && e.Data.Contains("call_1"));
        var evs2 = Translators.TranslateSse(delta, "openai", "claude", "m", state).ToList();
        evs2.ShouldContain(e => e.Data.Contains("input_json_delta"));
    }

    [Fact]
    public void ClaudePrepare_ensures_trailing_user_turn()
    {
        var o = JsonNode.Parse("""{"messages":[{"role":"assistant","content":[{"type":"text","text":"hi"}]}]}""")!.AsObject();
        ExtendedTranslators.ClaudePrepare(o);
        o["messages"]!.AsArray().Last()!["role"]!.GetValue<string>().ShouldBe("user");
    }

    [Fact]
    public void ClaudePrepare_fixes_dangling_tool_use()
    {
        var o = JsonNode.Parse("""{"messages":[{"role":"user","content":"hi"},{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"f","input":{}}]}]}""")!.AsObject();
        ExtendedTranslators.ClaudePrepare(o);
        var msgs = o["messages"]!.AsArray();
        msgs[2]!["content"]!.AsArray()[0]!["type"]!.GetValue<string>().ShouldBe("tool_result");
    }

    [Fact]
    public void MaxTokens_respects_claude_ceiling()
    {
        ExtendedTranslators.AdjustMaxTokens(200000, "claude-sonnet-4", "claude").ShouldBe(64000);
        ExtendedTranslators.AdjustMaxTokens(512, "claude-sonnet-4", "claude").ShouldBe(512);
        ExtendedTranslators.AdjustMaxTokens(200000, "gpt-5", "openai").ShouldBe(200000);
    }
}
