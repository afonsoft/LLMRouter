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
    public DbSet<RelayToken> RelayTokens => Set<RelayToken>();
    public DbSet<SessionPoolRow> SessionPools => Set<SessionPoolRow>();
    public DbSet<PoolSession> PoolSessions => Set<PoolSession>();
    public DbSet<CliToken> CliTokens => Set<CliToken>();
    public DbSet<VscodeToken> VscodeTokens => Set<VscodeToken>();
    public DbSet<EvalSuite> EvalSuites => Set<EvalSuite>();
    public DbSet<EvalCase> EvalCases => Set<EvalCase>();
    public DbSet<EvalRun> EvalRuns => Set<EvalRun>();
    public DbSet<A2aTask> A2aTasks => Set<A2aTask>();
    public DbSet<ConductorTask> ConductorTasks => Set<ConductorTask>();
    public DbSet<ModelCooldown> ModelCooldowns => Set<ModelCooldown>();
    public DbSet<FallbackChain> FallbackChains => Set<FallbackChain>();
    public DbSet<CapabilityOverride> CapabilityOverrides => Set<CapabilityOverride>();
    public DbSet<SyncedModel> SyncedModels => Set<SyncedModel>();
    public DbSet<QuotaWindow> QuotaWindows => Set<QuotaWindow>();
    public DbSet<CredentialExpiration> CredentialExpirations => Set<CredentialExpiration>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<KvEntry>().HasKey(e => new { e.Scope, e.Key });
        mb.Entity<ApiKey>().HasIndex(e => e.Key).IsUnique();
        mb.Entity<RelayToken>().HasIndex(e => e.Token).IsUnique();
        mb.Entity<Combo>().HasIndex(e => e.Name).IsUnique();
        mb.Entity<UsageRecord>().HasIndex(e => e.Timestamp);
        mb.Entity<UsageRecord>().HasIndex(e => e.Provider);
        mb.Entity<UsageRecord>().HasIndex(e => e.Model);
        mb.Entity<UsageRecord>().HasIndex(e => e.ConnectionId);
        mb.Entity<RequestDetail>().HasIndex(e => e.Timestamp);
        mb.Entity<RequestDetail>().HasIndex(e => e.Provider);
        mb.Entity<RequestDetail>().HasIndex(e => e.Model);
        mb.Entity<ProviderNode>().HasIndex(e => e.Type);
        mb.Entity<PoolSession>().HasIndex(e => e.PoolId);
        mb.Entity<CliToken>().HasIndex(e => e.Token).IsUnique();
        mb.Entity<VscodeToken>().HasIndex(e => e.Token).IsUnique();
        mb.Entity<EvalCase>().HasIndex(e => e.SuiteId);
        mb.Entity<EvalRun>().HasIndex(e => e.SuiteId);
        mb.Entity<A2aTask>().HasIndex(e => e.State);
        mb.Entity<ConductorTask>().HasIndex(e => e.State);
        mb.Entity<UsageRecord>().HasIndex(e => e.ApiKey);
        mb.Entity<ChatSession>().HasIndex(e => new { e.KeyId, e.Model });
        mb.Entity<ProviderConnection>().HasIndex(e => new { e.Provider, e.IsActive });
        mb.Entity<QuotaWindow>().HasIndex(e => e.Provider);
    }

    // SPEC-074: telemetry entity types never bust the hot cache — they are
    // written every request. Any other entity change invalidates the whole
    // hot cache (config writes are rare). SuppressCacheBust is set on the
    // UsageWriter's dedicated context so offloaded usage writes never bust.
    private static readonly HashSet<Type> TelemetryTypes =
    [
        typeof(UsageRecord), typeof(RequestDetail), typeof(UsageDaily),
        typeof(CompressionRun), typeof(JobRun), typeof(JobState),
        typeof(ChatSession), typeof(A2aTask), typeof(ConductorTask),
        typeof(EvalRun), typeof(CacheEntry), typeof(FileEntry),
        typeof(MetaEntry), typeof(PoolSession), typeof(ModelCooldown),
    ];
    private static readonly HashSet<string> TelemetryKvScopes =
        new(StringComparer.OrdinalIgnoreCase)
        { "audit", "quota", "quotaDaily", "connErrors", "usage", "stats", "keyusage", "connstate" };

    public bool SuppressCacheBust { get; set; }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var configChange = !SuppressCacheBust && ChangeTracker.Entries()
            .Any(e => e.State != EntityState.Unchanged && e.State != EntityState.Detached
                && (e.Entity is not KvEntry kv
                    ? !TelemetryTypes.Contains(e.Entity.GetType())
                    : !TelemetryKvScopes.Contains(kv.Scope.Split(':')[0])));
        var n = await base.SaveChangesAsync(cancellationToken);
        if (configChange) Gateway.HotCache.Default.InvalidateAll();
        return n;
    }

    public override int SaveChanges()
    {
        var n = base.SaveChanges();
        return n;
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
            // SPEC-082: combo invariants + composite tiers columns
            foreach (var col in new[]
            {
                "ALTER TABLE combos ADD COLUMN AllowedProviders TEXT NULL",
                "ALTER TABLE combos ADD COLUMN AllowedFamilies TEXT NULL",
                "ALTER TABLE combos ADD COLUMN Tiers TEXT NULL",
            })
            {
                cmd.CommandText = col;
                try { cmd.ExecuteNonQuery(); } catch { /* column already exists */ }
            }
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
                """CREATE TABLE IF NOT EXISTS relayTokens (Id TEXT NOT NULL PRIMARY KEY, Token TEXT NOT NULL, Name TEXT NULL, AllowedModels TEXT NOT NULL DEFAULT '[]', QuotaRequests INTEGER NOT NULL DEFAULT 0, QuotaTokens INTEGER NOT NULL DEFAULT 0, ExpiresAt TEXT NULL, IsActive INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS sessionPools (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Provider TEXT NOT NULL, Strategy TEXT NOT NULL DEFAULT 'round-robin', MinSize INTEGER NOT NULL DEFAULT 1, MaxSize INTEGER NOT NULL DEFAULT 5, LeaseSeconds INTEGER NOT NULL DEFAULT 60, IsActive INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS poolSessions (Id TEXT NOT NULL PRIMARY KEY, PoolId TEXT NOT NULL, ConnectionId TEXT NULL, State TEXT NOT NULL DEFAULT 'idle', Health TEXT NOT NULL DEFAULT 'healthy', BusyUntil TEXT NULL, CooldownUntil TEXT NULL, LastUsedAt TEXT NULL, TotalRequests INTEGER NOT NULL DEFAULT 0, SuccessfulRequests INTEGER NOT NULL DEFAULT 0, ConsecutiveFails INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS cliTokens (Id TEXT NOT NULL PRIMARY KEY, Token TEXT NOT NULL, State TEXT NOT NULL DEFAULT 'pending', DeviceName TEXT NULL, ApiKey TEXT NULL, CreatedAt TEXT NOT NULL, ApprovedAt TEXT NULL)""",
                """CREATE TABLE IF NOT EXISTS vscodeTokens (Id TEXT NOT NULL PRIMARY KEY, Token TEXT NOT NULL, Name TEXT NULL, DefaultCombo TEXT NULL, AllowedCombos TEXT NOT NULL DEFAULT '[]', IsActive INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS evalSuites (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS evalCases (Id TEXT NOT NULL PRIMARY KEY, SuiteId TEXT NOT NULL, Input TEXT NOT NULL, ExpectType TEXT NOT NULL DEFAULT 'contains', ExpectValue TEXT NOT NULL DEFAULT '', Weight REAL NOT NULL DEFAULT 1, Ord INTEGER NOT NULL DEFAULT 0)""",
                """CREATE TABLE IF NOT EXISTS evalRuns (Id TEXT NOT NULL PRIMARY KEY, SuiteId TEXT NOT NULL, Target TEXT NOT NULL, Status TEXT NOT NULL DEFAULT 'running', Score REAL NOT NULL DEFAULT 0, Results TEXT NOT NULL DEFAULT '[]', StartedAt TEXT NOT NULL, DurationMs INTEGER NOT NULL DEFAULT 0)""",
                """CREATE TABLE IF NOT EXISTS a2aTasks (Id TEXT NOT NULL PRIMARY KEY, Agent TEXT NULL, Payload TEXT NOT NULL DEFAULT '{}', State TEXT NOT NULL DEFAULT 'queued', Result TEXT NULL, Error TEXT NULL, CreatedAt TEXT NOT NULL, StartedAt TEXT NULL, FinishedAt TEXT NULL)""",
                """CREATE TABLE IF NOT EXISTS conductorTasks (Id TEXT NOT NULL PRIMARY KEY, Goal TEXT NOT NULL, Model TEXT NULL, Steps TEXT NOT NULL DEFAULT '[]', State TEXT NOT NULL DEFAULT 'queued', Result TEXT NULL, Error TEXT NULL, CreatedAt TEXT NOT NULL, FinishedAt TEXT NULL)""",
                """CREATE TABLE IF NOT EXISTS modelCooldowns (Id TEXT NOT NULL PRIMARY KEY, Provider TEXT NOT NULL, Model TEXT NOT NULL, Until TEXT NOT NULL, Reason TEXT NULL, CreatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS fallbackChains (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Steps TEXT NOT NULL DEFAULT '[]', Active INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS capabilityOverrides (Id TEXT NOT NULL PRIMARY KEY, Provider TEXT NOT NULL, Model TEXT NOT NULL, Capabilities TEXT NOT NULL DEFAULT '{}', UpdatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS syncedModels (Id TEXT NOT NULL PRIMARY KEY, Provider TEXT NOT NULL, Model TEXT NOT NULL, Available INTEGER NOT NULL DEFAULT 1, LastSyncAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS quotaWindows (Id TEXT NOT NULL PRIMARY KEY, Provider TEXT NOT NULL, Name TEXT NOT NULL, WindowMinutes INTEGER NOT NULL, MaxTokens INTEGER NOT NULL, MaxRequests INTEGER NOT NULL, CreatedAt TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS credentialExpirations (Id TEXT NOT NULL PRIMARY KEY, ConnectionId TEXT NOT NULL, ExpiresAt TEXT NOT NULL, WarnDays INTEGER NOT NULL DEFAULT 7)""",
                """CREATE TABLE IF NOT EXISTS tags (Id TEXT NOT NULL PRIMARY KEY, TargetType TEXT NOT NULL, TargetId TEXT NOT NULL, Value TEXT NOT NULL)""",
                """CREATE TABLE IF NOT EXISTS policies (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Priority INTEGER NOT NULL DEFAULT 0, Rule TEXT NOT NULL DEFAULT '{}', Enabled INTEGER NOT NULL DEFAULT 1)""",
                """CREATE TABLE IF NOT EXISTS chatSessions (Id TEXT NOT NULL PRIMARY KEY, KeyId TEXT NOT NULL, Model TEXT NOT NULL, StartedAt TEXT NOT NULL, LastSeenAt TEXT NOT NULL, MessageCount INTEGER NOT NULL DEFAULT 0)""",
                """CREATE INDEX IF NOT EXISTS IX_usageHistory_ApiKey ON usageHistory (ApiKey)""",
                """CREATE INDEX IF NOT EXISTS IX_chatSessions_KeyId_Model ON chatSessions (KeyId, Model)""",
                """CREATE INDEX IF NOT EXISTS IX_providerConnections_Provider_IsActive ON providerConnections (Provider, IsActive)""",
                """CREATE INDEX IF NOT EXISTS IX_quotaWindows_Provider ON quotaWindows (Provider)""",
            })
            {
                cmd.CommandText = ddl;
                try { cmd.ExecuteNonQuery(); } catch { /* exists */ }
            }
        }
        finally { if (opened) conn.Close(); }
    }
}
