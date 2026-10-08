using System.Text.Json;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

public class ComboPlannerTests
{
    [Fact]
    public void Fallback_preserves_order()
    {
        var models = new List<string> { "a/x", "b/y", "c/z" };
        ComboPlanner.GetRotatedModels(models, "c1", "fallback").ShouldBe(models);
    }

    [Fact]
    public void RoundRobin_rotates()
    {
        var models = new List<string> { "a/x", "b/y" };
        ComboPlanner.ResetRotation("rr");
        var first = ComboPlanner.GetRotatedModels(models, "rr", "round-robin");
        var second = ComboPlanner.GetRotatedModels(models, "rr", "round-robin");
        first[0].ShouldBe("a/x");
        second[0].ShouldBe("b/y");
    }

    [Fact]
    public void RoundRobin_sticky_limit()
    {
        var models = new List<string> { "a/x", "b/y" };
        ComboPlanner.ResetRotation("rr2");
        ComboPlanner.GetRotatedModels(models, "rr2", "round-robin", stickyLimit: 2)[0].ShouldBe("a/x");
        ComboPlanner.GetRotatedModels(models, "rr2", "round-robin", stickyLimit: 2)[0].ShouldBe("a/x");
        ComboPlanner.GetRotatedModels(models, "rr2", "round-robin", stickyLimit: 2)[0].ShouldBe("b/y");
    }

    [Fact]
    public void Detects_vision_requirement()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""{"messages":[{"role":"user","content":[{"type":"text","text":"hi"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AA"}}]}]}""");
        ComboPlanner.DetectRequiredCapabilities(body).ShouldContain("vision");
    }

    [Fact]
    public void Detects_pdf_requirement()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""{"messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"data:application/pdf;base64,AA"}}]}]}""");
        ComboPlanner.DetectRequiredCapabilities(body).ShouldContain("pdf");
    }

    [Fact]
    public void Reorder_never_drops_models()
    {
        var planner = new ComboPlanner(new ProviderRegistry());
        var models = new List<string> { "a/x", "b/y" };
        var reordered = planner.ReorderByCapabilities(models, ["vision"]);
        reordered.Count.ShouldBe(2);
    }
}
