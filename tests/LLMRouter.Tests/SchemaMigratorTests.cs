using LLMRouter.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

public class SchemaMigratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llmr-mig-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _dbPath;

    public SchemaMigratorTests()
    {
        _dbPath = Path.Combine(_dir, "test.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        // SQLite keeps WAL files around briefly — best effort cleanup.
        foreach (var f in Directory.Exists(_dir) ? Directory.GetFiles(_dir) : [])
            try { File.Delete(f); } catch { }
        try { Directory.Delete(_dir); } catch { }
    }

    private LlmRouterDbContext Db() => new(
        new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    private List<string> Tables()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        var names = new List<string>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) names.Add(rd.GetString(0));
        return names;
    }

    private long Count(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Fresh_db_boots_with_migrations_not_ensurecreated()
    {
        using var db = Db();
        SchemaMigrator.Apply(db, autoMigrate: true);
        Tables().ShouldContain("__EFMigrationsHistory");
        Tables().ShouldContain("providerConnections");
        Tables().ShouldContain("auditEvents");
        Count("SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%InitialCreate'").ShouldBe(1);
    }

    [Fact]
    public void Legacy_db_seeds_history_and_keeps_data()
    {
        // Simulate a pre-SPEC-109 database: full schema, data, no history table.
        using (var db = Db())
        {
            db.Database.EnsureCreated();
            db.Combos.Add(new Combo { Id = "cb1", Name = "legacy-combo", Models = "[\"openai/gpt-4\"]", CreatedAt = "c", UpdatedAt = "c" });
            db.SaveChanges();
        }
        Tables().ShouldNotContain("__EFMigrationsHistory");

        using (var db = Db())
        {
            SchemaMigrator.Apply(db, autoMigrate: true);
        }
        Count("SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%InitialCreate'").ShouldBe(1);
        Count("SELECT COUNT(*) FROM combos WHERE Name = 'legacy-combo'").ShouldBe(1);
    }

    [Fact]
    public void Second_boot_is_a_noop()
    {
        using (var db = Db()) SchemaMigrator.Apply(db, autoMigrate: true);
        var before = Count("SELECT COUNT(*) FROM __EFMigrationsHistory");
        using (var db = Db()) SchemaMigrator.Apply(db, autoMigrate: true);
        Count("SELECT COUNT(*) FROM __EFMigrationsHistory").ShouldBe(before);
    }

    [Fact]
    public void Skip_env_leaves_empty_db_empty()
    {
        Environment.SetEnvironmentVariable("LLMR_SKIP_MIGRATE", "1");
        try
        {
            using var db = Db();
            SchemaMigrator.ApplyFromEnvironment(db);
        }
        finally { Environment.SetEnvironmentVariable("LLMR_SKIP_MIGRATE", null); }
        Tables().ShouldBeEmpty();
    }

    [Fact]
    public void AutoMigrate_false_skips_migration()
    {
        using var db = Db();
        SchemaMigrator.Apply(db, autoMigrate: false);
        Tables().ShouldBeEmpty();
    }
}
