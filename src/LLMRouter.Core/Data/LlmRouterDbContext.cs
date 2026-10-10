using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Data;

public class LlmRouterDbContext : DbContext
{
    public LlmRouterDbContext(DbContextOptions<LlmRouterDbContext> options) : base(options) { }

    public DbSet<MetaEntry> Meta => Set<MetaEntry>();
    public DbSet<SettingRow> Settings => Set<SettingRow>();
    public DbSet<ProviderConnection> ProviderConnections => Set<ProviderConnection>();
    public DbSet<ProviderNode> ProviderNodes => Set<ProviderNode>();
    public DbSet<ProxyPool> ProxyPools => Set<ProxyPool>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Combo> Combos => Set<Combo>();
    public DbSet<KvEntry> Kv => Set<KvEntry>();
    public DbSet<UsageRecord> UsageHistory => Set<UsageRecord>();
    public DbSet<UsageDaily> UsageDaily => Set<UsageDaily>();
    public DbSet<RequestDetail> RequestDetails => Set<RequestDetail>();
    public DbSet<CompressionCombo> CompressionCombos => Set<CompressionCombo>();
    public DbSet<CompressionComboAssignment> CompressionComboAssignments => Set<CompressionComboAssignment>();
    public DbSet<CompressionRun> CompressionRuns => Set<CompressionRun>();
    public DbSet<JobState> JobStates => Set<JobState>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<RateLimit> RateLimits => Set<RateLimit>();
    public DbSet<FileEntry> Files => Set<FileEntry>();
    public DbSet<KeyGroup> KeyGroups => Set<KeyGroup>();
    public DbSet<QuotaPlan> QuotaPlans => Set<QuotaPlan>();
    public DbSet<QuotaSchedule> QuotaSchedules => Set<QuotaSchedule>();
    public DbSet<LogExportDestination> LogExportDestinations => Set<LogExportDestination>();
    public DbSet<PlaygroundPreset> PlaygroundPresets => Set<PlaygroundPreset>();
    public DbSet<CacheEntry> CacheEntries => Set<CacheEntry>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<KvEntry>().HasKey(e => new { e.Scope, e.Key });
        mb.Entity<ApiKey>().HasIndex(e => e.Key).IsUnique();
        mb.Entity<Combo>().HasIndex(e => e.Name).IsUnique();
        mb.Entity<UsageRecord>().HasIndex(e => e.Timestamp);
        mb.Entity<UsageRecord>().HasIndex(e => e.Provider);
        mb.Entity<UsageRecord>().HasIndex(e => e.Model);
        mb.Entity<UsageRecord>().HasIndex(e => e.ConnectionId);
        mb.Entity<RequestDetail>().HasIndex(e => e.Timestamp);
        mb.Entity<RequestDetail>().HasIndex(e => e.Provider);
        mb.Entity<RequestDetail>().HasIndex(e => e.Model);
        mb.Entity<ProviderNode>().HasIndex(e => e.Type);
    }

    // EnsureCreated isn't atomic across concurrent contexts (parallel WebApplicationFactory
    // hosts in tests) — serialize creation and tolerate a schema another host just created.
    private static readonly object EnsureCreatedLock = new();

    /// <summary>Creates the schema (single-file embedded DB, mirroring the upstream bootstrap).</summary>
    public void EnsureCreated()
    {
        lock (EnsureCreatedLock)
        {
            try { Database.EnsureCreated(); }
            catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            { /* schema raced by a parallel host */ }
        }
        // Lightweight migrations for DBs created before these tables/columns existed.
        var conn = Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE usageHistory ADD COLUMN LatencyMs INTEGER NOT NULL DEFAULT 0";
            try { cmd.ExecuteNonQuery(); } catch { /* column already exists */ }
            // SPEC-034: context-compression tables (no-op when EnsureCreated already made them)
            foreach (var ddl in new[]
            {
                """CREATE TABLE IF NOT EXISTS compressionCombos (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Description TEXT NULL, Pipeline TEXT NOT NULL DEFAULT '[]', LanguagePacks TEXT NOT NULL DEFAULT '[]', OutputMode INTEGER NOT NULL DEFAULT 0, OutputModeIntensity TEXT NULL, IsDefault INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS compressionComboAssignments (Id TEXT NOT NULL PRIMARY KEY, CompressionComboId TEXT NOT NULL, RoutingComboId TEXT NOT NULL, CreatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS compressionRuns (Id TEXT NOT NULL PRIMARY KEY, Timestamp TEXT NOT NULL, PrincipalId TEXT NULL, Model TEXT NULL, EngineId TEXT NOT NULL, BeforeChars INTEGER NOT NULL, AfterChars INTEGER NOT NULL, ComboId TEXT NULL)""",
                """CREATE TABLE IF NOT EXISTS jobStates (Id TEXT NOT NULL PRIMARY KEY, Enabled INTEGER NOT NULL DEFAULT 1, LastRun TEXT NULL, NextRun TEXT NULL, LastStatus TEXT NULL, LastError TEXT NULL, LastDurationMs INTEGER NOT NULL DEFAULT 0)""",
                """CREATE TABLE IF NOT EXISTS jobRuns (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, JobId TEXT NOT NULL, StartedAt TEXT NOT NULL, DurationMs INTEGER NOT NULL, Status TEXT NOT NULL, Output TEXT NULL)""",
                """CREATE TABLE IF NOT EXISTS rateLimits (Id TEXT NOT NULL PRIMARY KEY, Scope TEXT NOT NULL, ScopeValue TEXT NOT NULL, Rpm INTEGER NOT NULL DEFAULT 0, Tpm INTEGER NOT NULL DEFAULT 0, Burst INTEGER NOT NULL DEFAULT 0, Enabled INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS files (Id TEXT NOT NULL PRIMARY KEY, Filename TEXT NOT NULL, Bytes INTEGER NOT NULL DEFAULT 0, Purpose TEXT NOT NULL, Mime TEXT NOT NULL, CreatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS keyGroups (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, KeysJson TEXT NOT NULL DEFAULT '[]', CreatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS quotaPlans (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, LimitsJson TEXT NOT NULL DEFAULT '{}', Price REAL, CreatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS quotaSchedules (Id TEXT NOT NULL PRIMARY KEY, Target TEXT NOT NULL DEFAULT 'all', Window TEXT NOT NULL DEFAULT 'daily', LastRunAt TEXT, CreatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS cacheEntries (Id TEXT NOT NULL PRIMARY KEY, Hash TEXT NOT NULL, Provider TEXT NOT NULL, Model TEXT NOT NULL, Request TEXT NOT NULL, Response TEXT NOT NULL, Reasoning INTEGER NOT NULL DEFAULT 0, TokensSaved INTEGER NOT NULL DEFAULT 0, Hits INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL, ExpiresAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS playgroundPresets (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Model TEXT NOT NULL, ParamsJson TEXT NOT NULL DEFAULT '{}', CreatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS logExportDestinations (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Type TEXT NOT NULL, Config TEXT NOT NULL DEFAULT '{}', Filters TEXT NOT NULL DEFAULT '{}', Enabled INTEGER NOT NULL DEFAULT 1, LastRunAt TEXT, LastRunStatus TEXT, LastRunDetail TEXT, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)""",
            })
            {
                cmd.CommandText = ddl;
                try { cmd.ExecuteNonQuery(); } catch { /* exists */ }
            }
        }
        finally { if (opened) conn.Close(); }
    }
}
