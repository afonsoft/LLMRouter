using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

public class ModelResolverTests
{
    private readonly ModelResolver _r = new(new ProviderRegistry());

    [Fact]
    public void Parse_provider_slash_model()
    {
        var p = _r.Parse("openai/gpt-4o");
        p.Provider.ShouldBe("openai");
        p.Model.ShouldBe("gpt-4o");
    }

    [Fact]
    public void Resolve_bare_model_infers_provider()
    {
        var (p, m) = _r.Resolve("gpt-4o", null);
        p.ShouldBe("openai");
        m.ShouldBe("gpt-4o");
    }

    [Fact]
    public void Resolve_claude_infers_anthropic()
    {
        var (p, _) = _r.Resolve("claude-sonnet-4", null);
        p.ShouldBe("anthropic");
    }

    [Fact]
    public void Resolve_gemini_prefix()
    {
        var (p, _) = _r.Resolve("gemini-2.5-pro", null);
        p.ShouldBe("gemini");
    }

    [Fact]
    public void Resolve_alias_map()
    {
        var (p, m) = _r.Resolve("fast", new Dictionary<string, string> { ["fast"] = "openai/gpt-4o-mini" });
        p.ShouldBe("openai");
        m.ShouldBe("gpt-4o-mini");
    }

    [Fact]
    public void Resolve_gpt5_goes_codex()
    {
        var (p, _) = _r.Resolve("gpt-5.1-codex", null);
        p.ShouldBe("codex");
    }
}
