using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-008: quota exhaustion, proxy pool round-robin, token saver.</summary>
public class QuotaProxyTests : IDisposable
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

    [Fact]
    public async Task Quota_exhaustion_skips_connection()
    {
        using var db = Db();
        var conn = new ProviderConnection
        {
            Id = "c1", Provider = "openai", AuthType = "apiKey", IsActive = true,
            Data = """{"apiKey":"k","quotaDaily":100}""",
            CreatedAt = "2026-01-01 00:00:00", UpdatedAt = "2026-01-01 00:00:00",
        };
        db.ProviderConnections.Add(conn);
        // under cap
        db.UsageHistory.Add(new UsageRecord
        {
            Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            ConnectionId = "c1", PromptTokens = 40, CompletionTokens = 20, Status = "200",
        });
        db.SaveChanges();
        (await QuotaTracker.ExhaustedAsync(db, conn)).ShouldBeFalse();

        // push over the cap
        db.UsageHistory.Add(new UsageRecord
        {
            Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            ConnectionId = "c1", PromptTokens = 50, CompletionTokens = 0, Status = "200",
        });
        db.SaveChanges();
        (await QuotaTracker.ExhaustedAsync(db, conn)).ShouldBeTrue();

        // no caps → never exhausted
        conn.Data = """{"apiKey":"k"}""";
        (await QuotaTracker.ExhaustedAsync(db, conn)).ShouldBeFalse();
    }

    [Fact]
    public void Proxy_pool_round_robin_and_skip_inactive()
    {
        var pool = new ProxyPool
        {
            Id = "p1", IsActive = true,
            Data = """{"name":"x","proxies":[{"id":"a","url":"http://a:1","active":true,"failCount":0},{"id":"b","url":"http://b:1","active":true,"failCount":0},{"id":"c","url":"http://c:1","active":false,"failCount":0}]}""",
        };
        var picks = Enumerable.Range(0, 4).Select(_ => ProxyPoolService.Pick(pool)!.Url).ToList();
        picks.ShouldBe(new[] { "http://a:1", "http://b:1", "http://a:1", "http://b:1" });
        // inactive/exhausted never picked
        picks.ShouldNotContain("http://c:1");
    }

    [Fact]
    public void Proxy_pool_empty_when_all_burned()
    {
        var pool = new ProxyPool
        {
            Id = "p2", IsActive = true,
            Data = """{"proxies":[{"id":"a","url":"http://a:1","active":true,"failCount":5}]}""",
        };
        ProxyPoolService.Pick(pool).ShouldBeNull();
    }

    [Fact]
    public void Token_saver_compresses_and_counts()
    {
        var body = JsonSerializer.SerializeToElement(new
        {
            model = "m",
            messages = new[]
            {
                new { role = "user", content = "hello   world\n\n\nsame line\nsame line\ndone  " },
            },
        });
        var r = TokenSaver.Apply(body);
        r.SavedChars.ShouldBeGreaterThan(0);
        var content = r.Body.GetProperty("messages")[0].GetProperty("content").GetString()!;
        content.ShouldBe("hello world\nsame line\ndone");
        TokenSaver.SavedTokens(r.SavedChars).ShouldBe(r.SavedChars / 4);
    }
}
