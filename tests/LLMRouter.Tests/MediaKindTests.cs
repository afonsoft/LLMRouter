using Shouldly;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;

namespace LLMRouter.Tests;

public class MediaKindTests
{
    [Theory]
    [InlineData("/v1/audio/speech", "tts")]
    [InlineData("/v1/audio/voices", "tts")]
    [InlineData("/v1/audio/transcriptions", "stt")]
    [InlineData("/v1/images/generations", "image")]
    [InlineData("/v1/videos/generations", "video")]
    [InlineData("/v1/embeddings", "embedding")]
    [InlineData("/v1/search", "search")]
    [InlineData("/v1/web/fetch", "fetch")]
    [InlineData("/v1/systemone", "systemone")]
    [InlineData("/v1/chat/completions", null)]
    [InlineData("/v1/models", null)]
    public void KindForPath_maps_media_endpoints(string path, string? expected) =>
        MediaKinds.KindForPath(path).ShouldBe(expected);

    private static ProviderConnection Conn(string data) =>
        new() { Id = "c", Provider = "p", Name = "n", Data = data, IsActive = true };

    [Fact]
    public void KindsOf_reads_array_and_single_forms()
    {
        MediaKinds.KindsOf(Conn("""{"mediaKinds":["tts","stt"]}"""))
            .ShouldBe(new HashSet<string> { "tts", "stt" }, ignoreOrder: true);
        MediaKinds.KindsOf(Conn("""{"mediaKind":"image"}""")).ShouldContain("image");
        MediaKinds.KindsOf(Conn("{}")).ShouldBeEmpty();
        MediaKinds.KindsOf(Conn("not-json")).ShouldBeEmpty();
    }

    [Fact]
    public void Filter_prefers_kind_matching_connections()
    {
        var tts = Conn("""{"mediaKinds":["tts"]}""");
        var chat = Conn("{}");
        var targets = new List<ProviderConnection> { chat, tts };
        var r = MediaKinds.Filter(targets, "tts", c => c);
        r.ShouldBe([tts]);
    }

    [Fact]
    public void Filter_falls_back_to_all_when_no_kind_match()
    {
        var chat = Conn("{}");
        var r = MediaKinds.Filter([chat], "image", c => c);
        r.ShouldBe([chat]);
    }

    [Fact]
    public void Filter_is_noop_for_chat_paths()
    {
        var conns = new List<ProviderConnection> { Conn("{}") };
        MediaKinds.Filter(conns, MediaKinds.KindForPath("/v1/chat/completions"), c => c)
            .ShouldBe(conns);
    }
}
