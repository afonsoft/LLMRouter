using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-103: per-surface retention TTL + purge job.</summary>
public class Spec103RetentionTests : IDisposable
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

    private async Task SetRetentionAsync(LlmRouterDbContext db, string retentionJson)
    {
        var s = await db.Settings.FirstOrDefaultAsync()
            ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Data) ?? [];
        d["retention"] = JsonDocument.Parse(retentionJson).RootElement;
        s.Data = JsonSerializer.Serialize(d);
        await db.SaveChangesAsync();
    }

    private static string Iso(DateTime t) => t.ToString("O");
    private static string Legacy(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss");

    [Fact]
    public async Task Purge_deletes_only_expired_rows_per_surface()
    {
        using var db = Db();
        await SetRetentionAsync(db, """{"auditEventsDays":90,"requestDetailsDays":14,"usageDailyDays":0,"usageHistoryDays":0,"mcpToolCallsDays":0,"webhookDeliveriesDays":0}""");
        var old = DateTime.UtcNow.AddDays(-200);
        db.AuditEvents.AddRange(
            new AuditEvent { Action = "old", At = Iso(old) },
            new AuditEvent { Action = "new", At = Iso(DateTime.UtcNow) });
        db.RequestDetails.AddRange(
            new RequestDetail { Id = "r1", Timestamp = Legacy(old) },
            new RequestDetail { Id = "r2", Timestamp = Legacy(DateTime.UtcNow) });
        // never purged (days=0)
        db.UsageHistory.Add(new UsageRecord { Timestamp = Legacy(old), Tokens = "1" });
        await db.SaveChangesAsync();

        var result = await BuiltinJobs.RetentionPurgeJob.PurgeAsync(db, default);
        result.ShouldContain("auditEvents=1");
        result.ShouldContain("requestDetails=1");
        // SPEC-110: purge leaves a retention.purge anchor tombstone on top of
        // the retained row — count only non-tombstone events.
        (await db.AuditEvents.CountAsync(e => e.Action != "retention.purge")).ShouldBe(1);
        (await db.RequestDetails.CountAsync()).ShouldBe(1);
        (await db.UsageHistory.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Zero_days_disables_purge_for_that_table()
    {
        using var db = Db();
        await SetRetentionAsync(db, """{"auditEventsDays":0}""");
        db.AuditEvents.Add(new AuditEvent { Action = "ancient", At = Iso(DateTime.UtcNow.AddDays(-9999)) });
        await db.SaveChangesAsync();

        var result = await BuiltinJobs.RetentionPurgeJob.PurgeAsync(db, default);
        result.ShouldNotContain("auditEvents=");
        (await db.AuditEvents.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Chunked_delete_drains_more_than_one_batch()
    {
        using var db = Db();
        var old = Iso(DateTime.UtcNow.AddDays(-500));
        for (var i = 0; i < 5; i++)
            db.AuditEvents.Add(new AuditEvent { Action = $"e{i}", At = old });
        await db.SaveChangesAsync();

        // chunkSize 2 → needs 3 batches for 5 rows
        var n = await BuiltinJobs.RetentionPurgeJob.PurgeChunkedAsync(
            db, "auditEvents", "At", Iso(DateTime.UtcNow), default, chunkSize: 2);
        n.ShouldBe(5);
        (await db.AuditEvents.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Get_put_retention_round_trips_and_clamps_negatives()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var get0 = await client.GetFromJsonAsync<JsonElement>("/api/settings/retention");
        var ret0 = get0.GetProperty("retention");
        ret0.GetProperty("auditEventsDays").GetInt32().ShouldBe(90);
        ret0.GetProperty("usageDailyDays").GetInt32().ShouldBe(0);

        var put = await client.PutAsJsonAsync("/api/settings/retention",
            new { retention = new { auditEventsDays = 7, mcpToolCallsDays = -5 } });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ret1 = (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("retention");
        ret1.GetProperty("auditEventsDays").GetInt32().ShouldBe(7);
        ret1.GetProperty("mcpToolCallsDays").GetInt32().ShouldBe(0);

        var get1 = await client.GetFromJsonAsync<JsonElement>("/api/settings/retention");
        get1.GetProperty("retention").GetProperty("auditEventsDays").GetInt32().ShouldBe(7);
    }

    [Fact]
    public async Task Job_registered_and_enabled_in_scheduler()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var jobs = await client.GetFromJsonAsync<JsonElement>("/api/jobs");
        jobs.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("id").GetString())
            .ShouldContain("retention-purge");
    }
}
