using System.Net;
using System.Net.Http.Json;
using System.Text;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-049: session pools — acquire/release/lease/drain + gateway wiring.</summary>
public class SessionPoolTests : IDisposable
{
    private sealed class CapturingUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public SessionPoolTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                    c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
                .ConfigureServices(s =>
                    s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => new CapturingUpstream())));
    }

    public void Dispose()
    {
        _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db() => new(new DbContextOptionsBuilder<LlmRouterDbContext>()
        .UseSqlite($"Data Source={_dbPath}").Options);

    private async Task<SessionPoolRow> SeedPoolAsync(int min = 0, int max = 3, int lease = 60)
    {
        await using var db = Db();
        db.EnsureCreated();
        var pool = new SessionPoolRow
        {
            Id = "sp1", Name = "p", Provider = "openai",
            MinSize = min, MaxSize = max, LeaseSeconds = lease,
            CreatedAt = "x", UpdatedAt = "x",
        };
        db.SessionPools.Add(pool);
        await db.SaveChangesAsync();
        return pool;
    }

    [Fact]
    public async Task Acquire_returns_distinct_until_exhausted()
    {
        var pool = await SeedPoolAsync(min: 2, max: 3);
        await using var db = Db();
        await SessionPoolOps.EnsureMinAsync(db, pool);
        var a = await SessionPoolOps.AcquireAsync(db, pool);
        var b = await SessionPoolOps.AcquireAsync(db, pool);
        var c = await SessionPoolOps.AcquireAsync(db, pool); // grows to MaxSize
        var d = await SessionPoolOps.AcquireAsync(db, pool); // exhausted
        new[] { a!.Id, b!.Id, c!.Id }.Distinct().Count().ShouldBe(3);
        d.ShouldBeNull();
        a.State.ShouldBe("busy");
        a.BusyUntil.ShouldNotBeNull();
    }

    [Fact]
    public async Task Expired_lease_frees_busy_session()
    {
        var pool = await SeedPoolAsync(min: 0, max: 1, lease: 60);
        await using var db = Db();
        var a = await SessionPoolOps.AcquireAsync(db, pool);
        a.ShouldNotBeNull();
        (await SessionPoolOps.AcquireAsync(db, pool)).ShouldBeNull();
        // force the lease into the past → next acquire reclaims it
        a!.BusyUntil = DateTime.UtcNow.AddMinutes(-1).ToString("yyyy-MM-dd HH:mm:ss");
        await db.SaveChangesAsync();
        var reclaimed = await SessionPoolOps.AcquireAsync(db, pool);
        reclaimed.ShouldNotBeNull();
        reclaimed!.Id.ShouldBe(a.Id);
    }

    [Fact]
    public async Task Drain_resets_states()
    {
        var pool = await SeedPoolAsync(min: 2, max: 3);
        await using var db = Db();
        await SessionPoolOps.EnsureMinAsync(db, pool);
        var a = await SessionPoolOps.AcquireAsync(db, pool);
        await SessionPoolOps.ReleaseAsync(db, a!, success: false, rateLimited: true);
        a!.State.ShouldBe("cooldown");
        await SessionPoolOps.DrainAsync(db, pool);
        var sessions = await db.PoolSessions.Where(s => s.PoolId == pool.Id).ToListAsync();
        sessions.ShouldAllBe(s => s.State == "idle" && s.Health == "healthy");
    }

    [Fact]
    public async Task Three_fails_marks_session_dead_then_refresh_replaces()
    {
        var pool = await SeedPoolAsync(min: 1, max: 2);
        await using var db = Db();
        var s = await SessionPoolOps.AcquireAsync(db, pool);
        for (var i = 0; i < 3; i++)
            await SessionPoolOps.ReleaseAsync(db, s!, success: false);
        var dead = await db.PoolSessions.FindAsync(s!.Id);
        dead!.Health.ShouldBe("dead");
        await SessionPoolOps.RefreshAsync(db, pool);
        var live = await db.PoolSessions.Where(x => x.PoolId == pool.Id).ToListAsync();
        live.Count.ShouldBe(1);
        live[0].Id.ShouldNotBe(s.Id);
        live[0].State.ShouldBe("idle");
    }

    [Fact]
    public async Task Gateway_uses_pool_and_counts_requests()
    {
        // pool for provider "openai" + a connection + api key
        var pool = await SeedPoolAsync(min: 1, max: 2);
        await using (var db = Db())
        {
            db.ProviderConnections.Add(new ProviderConnection
            {
                Id = "c1", Provider = "openai", Name = "main", IsActive = true,
                Data = """{"baseUrl":"http://fake"}""", CreatedAt = "x", UpdatedAt = "x",
            });
            db.ApiKeys.Add(new ApiKey
            { Id = "k1", Key = "sk-rl-pool-test", IsActive = true, CreatedAt = "x" });
            await db.SaveChangesAsync();
        }
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("x-api-key", "sk-rl-pool-test");
        var r = await c.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "openai/gpt-4o",
            messages = new[] { new { role = "user", content = "hi" } },
            stream = false,
        });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var db = Db())
        {
            var s = await db.PoolSessions.FirstOrDefaultAsync(x => x.PoolId == pool.Id);
            s.ShouldNotBeNull();
            s!.TotalRequests.ShouldBe(1);
            s.SuccessfulRequests.ShouldBe(1);
            s.State.ShouldBe("idle"); // released after success
        }
    }
}
