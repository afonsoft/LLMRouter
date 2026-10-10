using LLMRouter.Core.Gateway;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-077: connection customHeaders — forbidden names, CR/LF, replace semantics.</summary>
public class CustomHeadersTests
{
    private static Dictionary<string, string> ApplyAll(string? dataJson)
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CustomHeaders.Apply(h, dataJson);
        return h;
    }

    [Fact]
    public void Applies_custom_headers_from_connection_data()
    {
        var h = ApplyAll("""{"customHeaders":{"anthropic-workspace-id":"ws-1","X-Team":"core"}}""");
        h["anthropic-workspace-id"].ShouldBe("ws-1");
        h["X-Team"].ShouldBe("core");
    }

    [Fact]
    public void Forbidden_auth_and_hop_by_hop_names_are_skipped()
    {
        var h = ApplyAll("""
            {"customHeaders":{
                "authorization":"Bearer evil","x-api-key":"k","cookie":"c","api-key":"k2",
                "host":"x","connection":"keep-alive","proxy-authorization":"p",
                "x-forwarded-for":"1.2.3.4","x-real-ip":"1.2.3.4","forwarded":"f","via":"v",
                "content-length":"5","transfer-encoding":"chunked",
                "x-ok":"yes"}}
            """);
        h.Count.ShouldBe(1);
        h["x-ok"].ShouldBe("yes");
    }

    [Fact]
    public void Crlf_in_name_or_value_is_dropped()
    {
        var h = ApplyAll("""{"customHeaders":{"x-a\r\nb":"v","x-good":"v\r\ninject"}}""");
        h.ShouldBeEmpty();
    }

    [Fact]
    public void Same_named_default_is_replaced_not_duplicated()
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["X-Env"] = "default", ["Other"] = "keep" };
        CustomHeaders.Apply(h, """{"customHeaders":{"x-env":"override"}}""");
        h.Count.ShouldBe(2);
        h["X-Env"].ShouldBe("override");
        h["Other"].ShouldBe("keep");
    }

    [Fact]
    public void Missing_or_malformed_custom_headers_is_a_noop()
    {
        ApplyAll("{}").ShouldBeEmpty();
        ApplyAll("""{"customHeaders":"nope"}""").ShouldBeEmpty();
        ApplyAll("""{"customHeaders":[1,2]}""").ShouldBeEmpty();
        ApplyAll("not json").ShouldBeEmpty();
        ApplyAll(null).ShouldBeEmpty();
        ApplyAll("""{"customHeaders":{"x-num":42,"x-ok":"v"}}""")["x-ok"].ShouldBe("v");
    }
}
