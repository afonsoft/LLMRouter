using LLMRouter.Core.Routing;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-004: round-robin sticky limits, aliases, model-combo mappings.</summary>
public class ComboAdvancedTests
{
    [Fact]
    public void RoundRobin_with_sticky_limit_repeats_model_then_rotates()
    {
        var models = new List<string> { "a/m1", "b/m2" };
        ComboPlanner.ResetRotation("c1");
        // sticky=3: first 3 calls start with m1, next 3 with m2
        for (var i = 0; i < 3; i++)
            ComboPlanner.GetRotatedModels(models, "c1", "round-robin", 3)[0].ShouldBe("a/m1");
        for (var i = 0; i < 3; i++)
            ComboPlanner.GetRotatedModels(models, "c1", "round-robin", 3)[0].ShouldBe("b/m2");
        ComboPlanner.GetRotatedModels(models, "c1", "round-robin", 3)[0].ShouldBe("a/m1");
    }

    [Fact]
    public void RoundRobin_sticky_1_alternates()
    {
        var models = new List<string> { "a/m1", "b/m2", "c/m3" };
        ComboPlanner.ResetRotation("c2");
        ComboPlanner.GetRotatedModels(models, "c2", "round-robin", 1)[0].ShouldBe("a/m1");
        ComboPlanner.GetRotatedModels(models, "c2", "round-robin", 1)[0].ShouldBe("b/m2");
        ComboPlanner.GetRotatedModels(models, "c2", "round-robin", 1)[0].ShouldBe("c/m3");
        ComboPlanner.GetRotatedModels(models, "c2", "round-robin", 1)[0].ShouldBe("a/m1");
    }

    [Fact]
    public void Fallback_does_not_rotate()
    {
        var models = new List<string> { "a/m1", "b/m2" };
        for (var i = 0; i < 4; i++)
            ComboPlanner.GetRotatedModels(models, "c3", "fallback", 1)[0].ShouldBe("a/m1");
    }
}
