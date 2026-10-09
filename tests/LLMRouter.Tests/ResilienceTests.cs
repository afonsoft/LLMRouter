using LLMRouter.Core.Resilience;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-021: provider breaker + model lockout state machines.</summary>
[Collection("StaticState")]
public class ResilienceTests : IDisposable
{
    public ResilienceTests() => ProviderBreaker.HerdWindow = TimeSpan.Zero;
    public void Dispose() => ProviderBreaker.HerdWindow = TimeSpan.FromSeconds(2);

    [Fact]
    public void Breaker_opens_after_threshold_and_blocks()
    {
        ProviderBreaker.ClearAll();
        for (var i = 0; i < 8; i++)
            ProviderBreaker.ReportStatus("t-oauth-1", 503, "oauth");
        ProviderBreaker.GetState("t-oauth-1", "oauth").ShouldBe(ProviderBreaker.State.Open);
        ProviderBreaker.CanExecute("t-oauth-1", "oauth").ShouldBeFalse();
        ProviderBreaker.Clear("t-oauth-1");
    }

    [Fact]
    public void Breaker_degrades_before_opening()
    {
        ProviderBreaker.ClearAll();
        for (var i = 0; i < 5; i++) // degrade=5, open=8 for oauth
            ProviderBreaker.ReportStatus("t-oauth-2", 502, "oauth");
        ProviderBreaker.GetState("t-oauth-2", "oauth").ShouldBe(ProviderBreaker.State.Degraded);
        ProviderBreaker.CanExecute("t-oauth-2", "oauth").ShouldBeTrue();
        ProviderBreaker.Clear("t-oauth-2");
    }

    [Fact]
    public void Breaker_success_resets_failures()
    {
        ProviderBreaker.ClearAll();
        for (var i = 0; i < 7; i++) ProviderBreaker.ReportStatus("t-apikey-1", 500, "apikey");
        ProviderBreaker.ReportSuccess("t-apikey-1");
        ProviderBreaker.GetState("t-apikey-1", "apikey").ShouldBe(ProviderBreaker.State.Closed);
        ProviderBreaker.Clear("t-apikey-1");
    }

    [Fact]
    public void Breaker_ignores_non_trip_statuses()
    {
        ProviderBreaker.ClearAll();
        for (var i = 0; i < 20; i++)
            ProviderBreaker.ReportStatus("t-no", 401, "apikey");
        ProviderBreaker.GetState("t-no", "apikey").ShouldBe(ProviderBreaker.State.Closed);
        ProviderBreaker.Clear("t-no");
    }

    [Fact]
    public void Lockout_scoped_to_model()
    {
        ModelLockout.IsModelScoped(404, "").ShouldBeTrue();
        ModelLockout.IsModelScoped(429, "rate limit for this model").ShouldBeTrue();
        ModelLockout.IsModelScoped(429, "account quota exceeded").ShouldBeFalse();
        ModelLockout.IsModelScoped(500, "").ShouldBeFalse();
    }

    [Fact]
    public void Lockout_lock_unlock_cycle()
    {
        ModelLockout.Lock("p1", "c1", "m1", TimeSpan.FromMinutes(1));
        ModelLockout.IsLocked("p1", "c1", "m1").ShouldBeTrue();
        ModelLockout.IsLocked("p1", "c1", "m2").ShouldBeFalse();
        ModelLockout.Unlock("p1", "c1", "m1");
        ModelLockout.IsLocked("p1", "c1", "m1").ShouldBeFalse();
    }
}
