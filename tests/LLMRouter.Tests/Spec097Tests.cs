using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using LLMRouter.Core.Translation;
using LLMRouter.Core.Usage;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-097 — upstream delta wave-3.</summary>
public class Spec097Tests
{
    // ---------- item 2: images nested in Responses tool outputs ----------

    [Fact]
    public void Vision_detected_in_function_call_output_items()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""
            {"input":[
              {"type":"function_call_output","call_id":"c1","output":[
                {"type":"input_image","image_url":"data:image/png;base64,AA"}
              ]}
            ]}
            """);
        ComboPlanner.DetectRequiredCapabilities(body).ShouldContain("vision");
    }

    [Fact]
    public void Vision_detected_in_custom_tool_call_output_items()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""
            {"input":[
              {"type":"custom_tool_call_output","call_id":"c2","output":[
                {"type":"image_url","image_url":{"url":"data:image/png;base64,AA"}}
              ]}
            ]}
            """);
        ComboPlanner.DetectRequiredCapabilities(body).ShouldContain("vision");
    }

    [Fact]
    public void Text_only_tool_output_does_not_add_vision()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""
            {"input":[
              {"type":"function_call_output","call_id":"c3","output":"plain text"}
            ]}
            """);
        ComboPlanner.DetectRequiredCapabilities(body).ShouldNotContain("vision");
    }

    // ---------- item 3: readable Responses compaction ----------

    private static string Enc(string text) =>
        "ocx1:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Compaction_item_restores_to_user_message()
    {
        var item = JsonNode.Parse($$"""{"type":"compaction","encrypted_content":"{{Enc("summary of prior work")}}"}""");
        var restored = ExtendedTranslators.ReadableCompaction.TryRestore(item)!;
        restored["role"]!.GetValue<string>().ShouldBe("user");
        var text = restored["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        text.ShouldStartWith(ExtendedTranslators.ReadableCompaction.SummaryPrefix);
        text.ShouldEndWith("summary of prior work");
    }

    [Fact]
    public void Compaction_types_all_recognized()
    {
        foreach (var t in new[] { "compaction", "compaction_summary", "context_compaction" })
            ExtendedTranslators.ReadableCompaction.IsCompactionType(t).ShouldBeTrue();
        ExtendedTranslators.ReadableCompaction.IsCompactionType("message").ShouldBeFalse();
    }

    [Fact]
    public void Malformed_compaction_payload_not_restored()
    {
        JsonNode.Parse("""{"type":"compaction","encrypted_content":"ocx1:not@@base64"}""")
            .Let(i => ExtendedTranslators.ReadableCompaction.TryRestore(i)).ShouldBeNull();
        JsonNode.Parse("""{"type":"compaction","encrypted_content":"ocx1:"}""")
            .Let(i => ExtendedTranslators.ReadableCompaction.TryRestore(i)).ShouldBeNull();
        JsonNode.Parse("""{"type":"compaction","encrypted_content":"zzzz"}""")
            .Let(i => ExtendedTranslators.ReadableCompaction.TryRestore(i)).ShouldBeNull();
        JsonNode.Parse("""{"type":"compaction"}""")
            .Let(i => ExtendedTranslators.ReadableCompaction.TryRestore(i)).ShouldBeNull();
    }

    [Fact]
    public void Bare_context_compaction_marker_is_passthrough()
    {
        JsonNode.Parse("""{"type":"context_compaction","id":"cc_1"}""")
            .Let(i => ExtendedTranslators.ReadableCompaction.IsBareMarker(i)).ShouldBeTrue();
        JsonNode.Parse("""{"type":"context_compaction","id":"cc_1","extra":1}""")
            .Let(i => ExtendedTranslators.ReadableCompaction.IsBareMarker(i)).ShouldBeFalse();
        JsonNode.Parse("""{"type":"compaction","id":"cc_1"}""")
            .Let(i => ExtendedTranslators.ReadableCompaction.IsBareMarker(i)).ShouldBeFalse();
    }

    [Fact]
    public void ResponsesToChatRequest_translates_compaction()
    {
        var req = JsonNode.Parse("""
            {"input":[
                {"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]},
                {"type":"compaction_summary","encrypted_content":""}
            ]}
            """.Replace(@"""encrypted_content"":""",
                $"\"encrypted_content\":\"{Enc("earlier decisions")}")) as JsonObject;
        var chat = ExtendedTranslators.ResponsesToChatRequest(req);
        var messages = chat["messages"]!.AsArray();
        messages.Count.ShouldBe(2);
        var content = messages[1]!["content"]!;
        var text = content is JsonArray parts
            ? parts[0]!["text"]!.GetValue<string>()
            : content.GetValue<string>();
        text.ShouldContain("earlier decisions");
        text.ShouldStartWith(ExtendedTranslators.ReadableCompaction.SummaryPrefix);
    }

    // ---------- item 4: provider-reported search cost ----------

    [Fact]
    public void Reported_cost_requires_opt_in()
    {
        var conn = new ProviderConnection { Id = "c1", Provider = "p", Data = "{}" };
        ReportedSearchCost.IsTrusted(conn).ShouldBeFalse();
        conn.Data = """{"trustReportedCost":true}""";
        ReportedSearchCost.IsTrusted(conn).ShouldBeTrue();
        conn.Data = """{"trustReportedCost":false}""";
        ReportedSearchCost.IsTrusted(conn).ShouldBeFalse();
    }

    [Fact]
    public void Reported_cost_parsed_and_clamped()
    {
        ReportedSearchCost.ReadReportedUsd("p1",
            """{"usage":{"request_id":"r-97a","search_cost_usd":0.0175}}""").ShouldBe(0.0175);
        // out of range / malformed -> null
        ReportedSearchCost.ReadReportedUsd("p1",
            """{"usage":{"search_cost_usd":150}}""").ShouldBeNull();
        ReportedSearchCost.ReadReportedUsd("p1",
            """{"usage":{"search_cost_usd":"oops"}}""").ShouldBeNull();
        ReportedSearchCost.ReadReportedUsd("p1", "not json").ShouldBeNull();
        ReportedSearchCost.ReadReportedUsd("p1", """{"usage":{}}""").ShouldBeNull();
    }

    [Fact]
    public void Reported_cost_dedupes_request_id()
    {
        var body = """{"usage":{"request_id":"r-dedupe-97","search_cost_usd":0.02}}""";
        ReportedSearchCost.ReadReportedUsd("pd", body).ShouldBe(0.02);
        ReportedSearchCost.ReadReportedUsd("pd", body).ShouldBeNull();
        // different provider scope counts separately
        ReportedSearchCost.ReadReportedUsd("pe", body).ShouldBe(0.02);
    }

    // ---------- item 10: registry additions ----------

    [Fact]
    public void Registry_wave3_entries_present()
    {
        var json = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "LLMRouter.Core", "Registry", "providers.json"))).RootElement;
        var ids = json.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();
        ids.ShouldContain("codebuddy-intl");
        var cb = json.EnumerateArray().First(e => e.GetProperty("id").GetString() == "codebuddy-intl");
        cb.GetProperty("authType").GetString().ShouldBe("oauth");
        cb.GetProperty("baseUrl").GetString().ShouldContain("codebuddy.ai");
        cb.GetProperty("models").GetArrayLength().ShouldBeGreaterThanOrEqualTo(20);
        var cursor = json.EnumerateArray().First(e => e.GetProperty("id").GetString() == "cursor-api");
        cursor.GetProperty("models").EnumerateArray()
            .Count(m => m.GetProperty("id").GetString()!.StartsWith("cursor-grok-4.7-"))
            .ShouldBe(16);
        var step = json.EnumerateArray().First(e => e.GetProperty("id").GetString() == "stepfun");
        step.GetProperty("models").EnumerateArray()
            .Any(m => m.GetProperty("id").GetString() == "step-5-preview").ShouldBeTrue();
        var zen = json.EnumerateArray().First(e => e.GetProperty("id").GetString() == "opencode-zen");
        zen.GetProperty("models").EnumerateArray()
            .Any(m => m.GetProperty("id").GetString() == "mimo-v2.6-flash-free").ShouldBeTrue();
    }
}

internal static class JsonNodeExt
{
    public static T Let<T>(this JsonNode? node, System.Func<JsonNode?, T> f) => f(node);
}
