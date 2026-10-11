using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Extras;
using LLMRouter.Core.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-110: tamper-evident audit hash chain — write path links
/// prevHash/hash, verify detects tampering, purge leaves an anchor
/// tombstone, export carries the columns.</summary>
public class Spec110AuditHashChainTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec110AuditHashChainTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
    }

    public void Dispose()
    {
        _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db() => new(new DbContextOptionsBuilder<LlmRouterDbContext>()
        .UseSqlite($"Data Source={_dbPath}").Options);

    private HttpClient Dash()
    {
        var c = _factory.CreateClient();
        var r = c.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Result;
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return c;
    }

    [Fact]
    public async Task Chain_verifies_end_to_end()
    {
        await using var db = Db();
        db.EnsureCreated();
        // EnsureCreated lacks the new columns on an in-place db — migrate.
        await db.Database.MigrateAsync();
        for (var i = 0; i < 3; i++)
            await AuditLog.RecordAsync(db, "test", $"x.act{i}", $"t{i}");
        var (ok, checked_, broken) = await AuditLog.VerifyAsync(db);
        ok.ShouldBeTrue();
        checked_.ShouldBe(3);
        broken.ShouldBeNull();
        // linked list sanity
        var rows = await db.AuditEvents.OrderBy(x => x.Id).ToListAsync();
        rows[0].PrevHash.ShouldBeNull();
        rows[1].PrevHash.ShouldBe(rows[0].Hash);
        rows[2].PrevHash.ShouldBe(rows[1].Hash);
    }

    [Fact]
    public async Task Tampered_middle_row_is_reported()
    {
        await using var db = Db();
        db.EnsureCreated();
        await db.Database.MigrateAsync();
        for (var i = 0; i < 3; i++)
            await AuditLog.RecordAsync(db, "test", $"x.act{i}", $"t{i}");
        var mid = await db.AuditEvents.OrderBy(x => x.Id).Skip(1).FirstAsync();
        mid.Target = "forged";
        await db.SaveChangesAsync();
        var (ok, _, broken) = await AuditLog.VerifyAsync(db);
        ok.ShouldBeFalse();
        broken.ShouldBe(mid.Id);
    }

    [Fact]
    public async Task Purge_keeps_verifiable_suffix_with_tombstone()
    {
        await using var db = Db();
        db.EnsureCreated();
        await db.Database.MigrateAsync();
        for (var i = 0; i < 6; i++)
            await AuditLog.RecordAsync(db, "test", $"x.act{i}", $"t{i}");
        // age the first three rows past a 1-day retention window
        var old = await db.AuditEvents.OrderBy(x => x.Id).Take(3).ToListAsync();
        foreach (var e in old)
        {
            e.At = DateTime.UtcNow.AddDays(-10).ToString("O");
            e.Hash = AuditLog.ChainHash(e); // re-seal after timestamp edit
            if (e.Id > 1)
            {
                var prev = await db.AuditEvents.FindAsync(e.Id - 1);
                e.PrevHash = prev?.Hash;
                e.Hash = AuditLog.ChainHash(e);
            }
        }
        await db.SaveChangesAsync();
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is null)
            db.Settings.Add(new SettingRow { Data = "{\"retention\":{\"auditEventsDays\":1}}" });
        else
            settings.Data = "{\"retention\":{\"auditEventsDays\":1}}";
        await db.SaveChangesAsync();

        await BuiltinJobs.RetentionPurgeJob.PurgeAsync(db, default);
        var rows = await db.AuditEvents.OrderBy(x => x.Id).ToListAsync();
        rows.ShouldContain(e => e.Action == "retention.purge");
        var (ok, checked_, broken) = await AuditLog.VerifyAsync(db);
        ok.ShouldBeTrue();
        checked_.ShouldBe(rows.Count(r => r.Hash != null));
        broken.ShouldBeNull();
    }

    [Fact]
    public async Task Export_csv_carries_hash_columns()
    {
        await using var db = Db();
        db.EnsureCreated();
        await db.Database.MigrateAsync();
        await AuditLog.RecordAsync(db, "test", "x.act", "t");
        var r = await Dash().GetAsync("/api/compliance/audit-log?format=csv");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var csv = await r.Content.ReadAsStringAsync();
        csv.ShouldContain("prevHash,hash");
    }

    [Fact]
    public async Task Backfill_chains_legacy_rows()
    {
        await using var db = Db();
        db.EnsureCreated();
        await db.Database.MigrateAsync();
        // write two rows bypassing the chain (legacy shape)
        for (var i = 0; i < 2; i++)
            db.AuditEvents.Add(new AuditEvent
            {
                Actor = "old", Action = $"legacy.{i}", At = DateTime.UtcNow.ToString("O"),
            });
        await db.SaveChangesAsync();
        (await AuditLog.BackfillAsync(db)).ShouldBe(2);
        (await AuditLog.BackfillAsync(db)).ShouldBe(0); // idempotent
        var (ok, checked_, _) = await AuditLog.VerifyAsync(db);
        ok.ShouldBeTrue();
        checked_.ShouldBe(2);
    }
}
