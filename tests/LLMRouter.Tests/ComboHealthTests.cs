using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Resilience;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-076: typed availability, stale combo refs + quota-window scoring.</summary>
[Collection("StaticState")]
public class ComboHealthTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db()
    {
        var ctx = new LlmRouterDbContext(
            new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        ctx.EnsureCreated();
        return ctx;
    }

    // ---- typed provider availability (upstream #15918) ----

    [Fact]
    public void Availability_no_credential_and_disabled()
    {
        ProviderAvailability.Resolve(new ProviderAvailability.Input(HasCredential: false))
            .State.ShouldBe("NO_CREDENTIAL");
        ProviderAvailability.Resolve(new ProviderAvailability.Input(IsActive: false))
            .State.ShouldBe("DISABLED");
    }

    [Fact]
    public void Availability_terminal_fresh_states()
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        ProviderAvailability.Resolve(new(TestStatus: "expired", LastErrorAt: now))
            .ShouldBe(new ProviderAvailability.Availability("AUTH_EXPIRED", Action: "REAUTHENTICATE"));
        ProviderAvailability.Resolve(new(TestStatus: "credits_exhausted", LastErrorAt: now))
            .State.ShouldBe("QUOTA_EXHAUSTED");
        ProviderAvailability.Resolve(new(TestStatus: "banned", LastErrorAt: now))
            .State.ShouldBe("DISABLED");
    }

    [Fact]
    public void Availability_stale_terminal_never_returns_to_available()
    {
        var old = DateTimeOffset.UtcNow.AddHours(-25).ToString("o");
        var a = ProviderAvailability.Resolve(new(TestStatus: "expired", LastErrorAt: old));
        a.State.ShouldBe("STALE_TERMINAL");
        a.PreviousState.ShouldBe("expired");
        // unverifiable terminal (no timestamp) is also stale
        ProviderAvailability.Resolve(new(TestStatus: "banned")).State.ShouldBe("STALE_TERMINAL");
    }

    [Fact]
    public void Availability_cooldown_or_error_is_retryable_unhealthy()
    {
        var future = DateTimeOffset.UtcNow.AddMinutes(2).ToString("o");
        ProviderAvailability.Resolve(new(TestStatus: "ok", RateLimitedUntil: future))
            .ShouldBe(new ProviderAvailability.Availability("UNHEALTHY", Retryable: true));
        ProviderAvailability.Resolve(new(TestStatus: "ok", LastErrorType: "network"))
            .State.ShouldBe("UNHEALTHY");
        ProviderAvailability.Resolve(new(TestStatus: "ok")).State.ShouldBe("AVAILABLE");
    }

    [Fact]
    public void Availability_quota_exhausted_reports_recheck_time()
    {
        var now = DateTimeOffset.UtcNow;
        var recheck = now.AddMinutes(30);
        var a = ProviderAvailability.Resolve(new(
            TestStatus: "credits_exhausted",
            LastErrorAt: now.ToString("o"),
            RateLimitedUntil: recheck.ToUnixTimeMilliseconds().ToString()));
        a.State.ShouldBe("QUOTA_EXHAUSTED");
        DateTimeOffset.Parse(a.NextEligibleRecheckAt!).ShouldBe(
            DateTimeOffset.FromUnixTimeMilliseconds(recheck.ToUnixTimeMilliseconds()));
    }

    // ---- stale combo model refs (upstream #13505 + #15925) ----

    [Fact]
    public async Task StaleRefs_flags_steps_missing_from_synced_catalog()
    {
        using var db = Db();
        db.Combos.Add(new Combo
        {
            Id = "c1", Name = "mix", CreatedAt = "x", UpdatedAt = "x",
            Models = """["openai/gpt-1","openai/gpt-2","anthropic/claude"]""",
        });
        db.SyncedModels.Add(new SyncedModel
        { Id = "s1", Provider = "openai", Model = "gpt-1", Available = true, LastSyncAt = "x" });
        db.SyncedModels.Add(new SyncedModel
        { Id = "s2", Provider = "openai", Model = "gpt-2", Available = false, LastSyncAt = "x" });
        await db.SaveChangesAsync();

        var refs = await StaleComboRefs.FindAsync(db, "openai");
        refs.Count.ShouldBe(1);
        refs[0].Step.ShouldBe("openai/gpt-2");
        // anthropic steps are untouched — FindAsync is scoped to the synced provider
        (await StaleComboRefs.FindAsync(db, "anthropic")).ShouldBeEmpty();
    }

    [Fact]
    public async Task StaleRefs_empty_sync_flags_nothing()
    {
        using var db = Db();
        db.Combos.Add(new Combo
        { Id = "c1", Name = "mix", CreatedAt = "x", UpdatedAt = "x", Models = """["openai/gpt-9"]""" });
        await db.SaveChangesAsync();
        (await StaleComboRefs.FindAsync(db, "openai")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Prune_removes_stale_steps_but_never_empties_a_combo()
    {
        using var db = Db();
        db.Combos.Add(new Combo
        {
            Id = "c1", Name = "partial", CreatedAt = "x", UpdatedAt = "x",
            Models = """["openai/good","openai/gone"]""",
        });
        db.Combos.Add(new Combo
        {
            Id = "c2", Name = "allstale", CreatedAt = "x", UpdatedAt = "x",
            Models = """["openai/gone"]""",
        });
        await db.SaveChangesAsync();

        var refs = new List<StaleComboRefs.Ref>
        {
            new("c1", "partial", "openai/gone"),
            new("c2", "allstale", "openai/gone"),
        };
        var pruned = await StaleComboRefs.PruneAsync(db, refs);
        pruned.Count.ShouldBe(1);
        pruned[0].ComboId.ShouldBe("c1");
        (await db.Combos.FindAsync("c1"))!.Models.ShouldBe("""["openai/good"]""");
        // c2's only step was stale → combo left untouched
        (await db.Combos.FindAsync("c2"))!.Models.ShouldBe("""["openai/gone"]""");
    }

    // ---- quota-window-scoped scoring (upstream #16054) ----

    [Fact]
    public async Task Headroom_orders_by_window_fraction_scoped_to_model_provider()
    {
        using (var db = Db())
        {
            // openai window nearly saturated; anthropic has room
            db.QuotaWindows.Add(new QuotaWindow
            { Id = "w1", Provider = "openai", Name = "rpm", WindowMinutes = 60, MaxRequests = 100, CreatedAt = "x" });
            db.QuotaWindows.Add(new QuotaWindow
            { Id = "w2", Provider = "anthropic", Name = "rpm", WindowMinutes = 60, MaxRequests = 100, CreatedAt = "x" });
            for (var i = 0; i < 95; i++)
                db.UsageHistory.Add(new UsageRecord
                {
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                    Provider = "openai", Model = "a", ConnectionId = "cx",
                });
            db.UsageHistory.Add(new UsageRecord
            {
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                Provider = "anthropic", Model = "b", ConnectionId = "cy",
            });
            await db.SaveChangesAsync();
        }
        using (var db = Db())
        {
            var ordered = await ComboStrategies.OrderAsync(
                "headroom", "c", new List<string> { "openai/a", "anthropic/b" }, db, new ProviderRegistry());
            ordered[0].ShouldBe("anthropic/b");
        }
    }

    [Fact]
    public async Task RemainingFraction_exhausted_window_scores_zero()
    {
        using var db = Db();
        db.QuotaWindows.Add(new QuotaWindow
        { Id = "w1", Provider = "openai", Name = "tok", WindowMinutes = 60, MaxTokens = 100, CreatedAt = "x" });
        db.UsageHistory.Add(new UsageRecord
        {
            Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            Provider = "openai", Model = "a", PromptTokens = 150, ConnectionId = "cx",
        });
        await db.SaveChangesAsync();
        (await QuotaWindows.RemainingFractionAsync(db, "openai")).ShouldBe(0);
        (await QuotaWindows.RemainingFractionAsync(db, "other")).ShouldBe(1);
    }
}
