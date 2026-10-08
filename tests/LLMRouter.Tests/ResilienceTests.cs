using LLMRouter.Core.Resilience;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-006: cooldown state machine.</summary>
public class ResilienceTests
{
    [Fact]
    public void Connection_cools_down_after_threshold_failures()
    {
        var id = $"t-{Guid.NewGuid():N}";
        CooldownTracker.IsCooling(id).ShouldBeFalse();
        for (var i = 0; i < CooldownTracker.FailureThreshold; i++)
            CooldownTracker.ReportFailure(id);
        CooldownTracker.IsCooling(id).ShouldBeTrue();
    }

    [Fact]
    public void Success_clears_cooldown()
    {
        var id = $"t-{Guid.NewGuid():N}";
        for (var i = 0; i < 5; i++) CooldownTracker.ReportFailure(id);
        CooldownTracker.IsCooling(id).ShouldBeTrue();
        CooldownTracker.ReportSuccess(id);
        CooldownTracker.IsCooling(id).ShouldBeFalse();
    }

    [Fact]
    public void Clear_removes_entry()
    {
        var id = $"t-{Guid.NewGuid():N}";
        for (var i = 0; i < 4; i++) CooldownTracker.ReportFailure(id);
        CooldownTracker.Clear(id);
        CooldownTracker.IsCooling(id).ShouldBeFalse();
        CooldownTracker.Snapshot().ShouldNotContain(x => x.ConnectionId == id);
    }

    [Fact]
    public void Below_threshold_does_not_cool()
    {
        var id = $"t-{Guid.NewGuid():N}";
        for (var i = 0; i < CooldownTracker.FailureThreshold - 1; i++)
            CooldownTracker.ReportFailure(id);
        CooldownTracker.IsCooling(id).ShouldBeFalse();
    }
}
