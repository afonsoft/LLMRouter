using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Extras;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Translation;
using LLMRouter.Server.Endpoints;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-075 — protocol parity vs OmniRoute 3.8.52.</summary>
public class ProtocolParityTests
{
    // ---------- error sanitization ----------

    [Fact]
    public void SanitizeMessage_redacts_known_credential_shapes()
    {
        var msg = "auth failed: sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789 and ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var out_ = ErrorSanitizer.SanitizeMessage(msg);
        out_.ShouldNotContain("sk-ant-");
        out_.ShouldNotContain("ghp_");
        out_.ShouldContain("[REDACTED");
    }

    [Fact]
    public void SanitizeMessage_redacts_labeled_assignments()
    {
        ErrorSanitizer.SanitizeMessage("denied api_key=supersecretvalue123").ShouldContain("api_key=[REDACTED]");
        ErrorSanitizer.SanitizeMessage("""{"access_token":"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc1234567890"}""")
            .ShouldNotContain("eyJhbGciOiJIUzI1NiJ9");
        ErrorSanitizer.SanitizeMessage("retry with --token s3cr3t").ShouldContain("[REDACTED]");
    }

    [Fact]
    public void SanitizeMessage_redacts_url_userinfo_and_sensitive_query()
    {
        var out_ = ErrorSanitizer.SanitizeMessage(
            "failed https://user:passw0rd@example.com/x?api_key=k123456&a=b");
        out_.ShouldContain("[REDACTED]@example.com");
        out_.ShouldContain("redacted=[REDACTED]");
        out_.ShouldNotContain("passw0rd");
        out_.ShouldNotContain("k123456");
    }

    [Fact]
    public void SanitizeMessage_strips_stack_tail_and_paths()
    {
        var out_ = ErrorSanitizer.SanitizeMessage(
            "request failed\n    at fetch (/srv/app/node_modules/x/client.ts:44:9)\n    at run (/srv/app/h.ts:1:2)");
        out_.ShouldBe("request failed");
        var withPath = ErrorSanitizer.SanitizeMessage("ENOENT open /var/secret/prod.key");
        withPath.ShouldContain("<path>/prod.key");
        withPath.ShouldNotContain("/var/secret");
    }

    [Fact]
    public void SanitizeMessage_redacts_pem_and_bearer_and_dataurl()
    {
        var pem = "-----BEGIN PRIVATE KEY-----\nMIIabc\n-----END PRIVATE KEY-----";
        ErrorSanitizer.SanitizeMessage($"tls {pem}").ShouldNotContain("MIIabc");
        ErrorSanitizer.SanitizeMessage("header Bearer abcdef1234567890").ShouldContain("Bearer [REDACTED]");
        ErrorSanitizer.SanitizeMessage("img data:image/png;base64,iVBORw0KGgoAAAANS").ShouldContain("[REDACTED_DATA_URL]");
    }

    [Fact]
    public void ToJsonErrorPayload_allowlists_fields_and_sanitizes()
    {
        var body = """{"error":{"message":"bad key sk-proj-aaaaaaaaaaaaaaaaaaaaaaaaaaaa","type":"invalid_request_error","secret":"x","stack":"trace"}}""";
        var payload = ErrorSanitizer.ToJsonErrorPayload(body);
        var err = (Dictionary<string, object?>)payload["error"]!;
        err["type"].ShouldBe("invalid_request_error");
        err.ShouldNotContainKey("secret");
        err.ShouldNotContainKey("stack");
        err["message"]!.ToString()!.ShouldContain("[REDACTED");
    }

    [Fact]
    public void ToJsonErrorPayload_plain_text_and_fallback()
    {
        var err = (Dictionary<string, object?>)ErrorSanitizer.ToJsonErrorPayload(
            "boom api_key=hunter2hunter2")["error"]!;
        err["message"]!.ToString()!.ShouldContain("[REDACTED]");
        var fb = (Dictionary<string, object?>)ErrorSanitizer.ToJsonErrorPayload("   ")["error"]!;
        fb["message"].ShouldBe("Upstream request failed.");
        // message always present even for weird payloads
        var arr = (Dictionary<string, object?>)ErrorSanitizer.ToJsonErrorPayload("[1,2]")["error"]!;
        arr["message"].ShouldBe("Upstream request failed.");
    }

    [Fact]
    public void SanitizeUpstreamDetails_drops_credential_keys()
    {
        var doc = JsonDocument.Parse(
            """{"provider":"anthropic","api_key":"sk-ant-api03-abcdefghijklmnopqrstuv","nested":{"password":"x","ok":1}}""");
        var cleaned = (Dictionary<string, object?>)ErrorSanitizer.SanitizeUpstreamDetails(doc.RootElement)!;
        cleaned["provider"].ShouldBe("anthropic");
        cleaned.ShouldNotContainKey("api_key");
        ((Dictionary<string, object?>)cleaned["nested"]!).ShouldNotContainKey("password");
    }

    // ---------- Responses refusal / phase ----------

    [Fact]
    public void Responses_refusal_delta_maps_to_refusal_field()
    {
        var events = Translators.TranslateSse(
            """{"type":"response.refusal.delta","delta":"I cannot"}""",
            "responsesApi", "openai", "m", new Translators.SseState()).ToList();
        events.Count.ShouldBe(1);
        events[0].Data.ShouldContain("\"refusal\":\"I cannot\"");
        events[0].Data.ShouldNotContain("\"content\":");
    }

    [Fact]
    public void Responses_text_delta_passes_phase()
    {
        var events = Translators.TranslateSse(
            """{"type":"response.output_text.delta","delta":"hi","phase":"commentary"}""",
            "responsesApi", "openai", "m", new Translators.SseState()).ToList();
        events.Count.ShouldBe(1);
        events[0].Data.ShouldContain("\"phase\":\"commentary\"");
        events[0].Data.ShouldContain("\"content\":\"hi\"");
        // absent phase → no phase key
        var plain = Translators.TranslateSse(
            """{"type":"response.output_text.delta","delta":"hi"}""",
            "responsesApi", "openai", "m", new Translators.SseState()).ToList();
        plain[0].Data.ShouldNotContain("phase");
    }

    // ---------- bedrock same-role merge ----------

    private static JsonObject Req(params JsonObject[] msgs) =>
        new() { ["messages"] = new JsonArray(msgs.Cast<JsonNode?>().ToArray()) };

    private static JsonObject Msg(string role, params object[] blocks)
    {
        var content = new JsonArray();
        foreach (var b in blocks) content.Add(b is string s ? new JsonObject { ["text"] = s } : (JsonNode)b);
        return new JsonObject { ["role"] = role, ["content"] = content };
    }

    [Fact]
    public void Bedrock_merge_consecutive_same_role()
    {
        var req = Req(
            Msg("user", "hello"),
            Msg("user", "world"),
            Msg("assistant", "ok"));
        ExtendedTranslators.MergeBedrockSameRoleMessages(req);
        var msgs = req["messages"]!.AsArray();
        msgs.Count.ShouldBe(2);
        msgs[0]!["content"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void Bedrock_plain_user_does_not_absorb_tool_result()
    {
        var toolResult = new JsonObject
        {
            ["toolResult"] = new JsonObject { ["toolUseId"] = "t1", ["content"] = "data" },
        };
        var req = Req(
            Msg("user", "plain"),
            Msg("user", toolResult));
        ExtendedTranslators.MergeBedrockSameRoleMessages(req);
        req["messages"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void Bedrock_tool_result_then_plain_user_merges_and_drops_filler()
    {
        var toolResult = new JsonObject
        {
            ["toolResult"] = new JsonObject { ["toolUseId"] = "t1", ["content"] = "data" },
        };
        var req = Req(
            Msg("user", toolResult),
            Msg("user", new JsonObject { ["text"] = " " }, "real"));
        ExtendedTranslators.MergeBedrockSameRoleMessages(req);
        var msgs = req["messages"]!.AsArray();
        msgs.Count.ShouldBe(1);
        var content = msgs[0]!["content"]!.AsArray();
        content.Count.ShouldBe(2); // toolResult + "real"; filler dropped
    }

    // ---------- anthropic rate-limit windows ----------

    [Fact]
    public void AnthropicQuotaProbe_reads_windows()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK);
        resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-requests-limit", "50");
        resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-requests-remaining", "40");
        resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-requests-reset", "2026-10-10T00:00:00Z");
        resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-tokens-limit", "40000");
        resp.Headers.TryAddWithoutValidation("anthropic-ratelimit-tokens-remaining", "39000");
        var quotas = ProviderOpsEndpoints.AnthropicQuotaProbe.ReadWindows(resp);
        quotas.Count.ShouldBe(2);
        var req = (Dictionary<string, object?>)quotas["requests"]!;
        ((double)req["total"]!).ShouldBe(50);
        ((double)req["remaining"]!).ShouldBe(40);
        req["displayName"].ShouldBe("Requests");
        var empty = ProviderOpsEndpoints.AnthropicQuotaProbe.ReadWindows(
            new HttpResponseMessage(HttpStatusCode.OK));
        empty.Count.ShouldBe(0);
    }

    // ---------- notion text sanitize ----------

    [Theory]
    [InlineData("﻿hello", "hello")]
    [InlineData("<lang>en</lang> hi", "en hi")]
    [InlineData("<lang", "")]
    [InlineData("plain text", "plain text")]
    public void SanitizeNotionText_cases(string input, string expected) =>
        MemoryStore.SanitizeNotionText(input).ShouldBe(expected);
}
