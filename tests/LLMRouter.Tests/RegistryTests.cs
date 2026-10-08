using LLMRouter.Core.Registry;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

public class RegistryTests
{
    private readonly ProviderRegistry _r = new();

    [Fact]
    public void Loads_registry()
    {
        _r.GetProvider("openai").ShouldNotBeNull();
        _r.GetProvider("anthropic").ShouldNotBeNull();
        _r.GetProvider("gemini").ShouldNotBeNull();
    }

    [Fact]
    public void Resolves_aliases()
    {
        _r.ResolveProviderId("openai").ShouldBe("openai");
    }

    [Fact]
    public void Ui_catalog_loaded()
    {
        _r.UiProviders().ShouldNotBeEmpty();
    }

    [Fact]
    public void Provider_has_models()
    {
        var p = _r.GetProvider("openai")!;
        p.Format.ShouldBe("openai");
        p.Models.ShouldNotBeNull();
    }
}
