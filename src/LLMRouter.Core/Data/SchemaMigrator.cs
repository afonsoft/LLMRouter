using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Data;

/// <summary>
/// SPEC-109: schema management via EF Core migrations.
/// Boot flow: seed <c>__EFMigrationsHistory</c> for databases created before
/// migrations existed (schema present, history absent → mark
/// <c>InitialCreate</c> applied), then <c>Database.Migrate()</c>. The legacy
/// idempotent DDL backfill stays as a safety net for databases older than the
/// baseline (tables/columns the baseline assumes but never had lazy DDL
/// coverage, e.g. mid-era deployments).
/// Gates: <c>settings.database.autoMigrate</c> (default true) and
/// <c>LLMR_SKIP_MIGRATE=1</c> for exotic recovery.
/// </summary>
public static class SchemaMigrator
{
    // Serialize creation/migration across concurrent contexts (parallel
    // WebApplicationFactory hosts in tests); tolerate a schema another host
    // just created.
    private static readonly object MigrateLock = new();

    /// <summary>
    /// Boot-time entry point honoring <c>LLMR_SKIP_MIGRATE</c> and
    /// <c>settings.database.autoMigrate</c>. <paramref name="log"/> receives a
    /// one-line reason when the migration is skipped.
    /// </summary>
    public static void ApplyFromEnvironment(LlmRouterDbContext db, Action<string>? log = null)
    {
        if (Environment.GetEnvironmentVariable("LLMR_SKIP_MIGRATE") == "1")
        {
            log?.Invoke("LLMR_SKIP_MIGRATE=1 — schema migration skipped");
            return;
        }
        var autoMigrate = ReadAutoMigrateSetting(db);
        if (autoMigrate is false)
        {
            log?.Invoke("settings.database.autoMigrate=false — schema migration skipped");
            return;
        }
        Apply(db, autoMigrate: true);
    }

    /// <summary>Migrates the schema when <paramref name="autoMigrate"/> is true.</summary>
    public static void Apply(LlmRouterDbContext db, bool autoMigrate)
    {
        if (!autoMigrate) return;
        lock (MigrateLock)
        {
            if (!db.Database.IsRelational())
            {
                // InMemory test provider — no migrations, just build the model.
                try { db.Database.EnsureCreated(); }
                catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                { /* schema raced by a parallel host */ }
                return;
            }

            SeedMigrationsHistory(db);
            try { db.Database.Migrate(); }
            catch (Exception ex) when (SchemaAhead(ex))
            {
                // "already exists" (table raced by a parallel host) or "duplicate
                // column name" (schema created by EnsureCreated/current model —
                // newer than the baseline the history seed assumes). Both mean
                // the schema is already at or ahead of what the failing
                // migration would produce; the backfill covers real gaps.
            }
            LegacyBackfill(db);
        }
    }

    /// <summary>
    /// Databases created before SPEC-109 have the full schema but no
    /// <c>__EFMigrationsHistory</c>. Detect any user table and record the
    /// baseline migration as applied so <c>Migrate()</c> is a no-op; the
    /// backfill below covers any gaps versus the baseline.
    /// </summary>
    private static bool SchemaAhead(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void SeedMigrationsHistory(LlmRouterDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                )
                """;
            cmd.ExecuteNonQuery();
            cmd.CommandText = """
                INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                SELECT '20261011123316_InitialCreate', '10.0.0'
                WHERE EXISTS (
                    SELECT 1 FROM sqlite_master
                    WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'
                    LIMIT 1)
                """;
            cmd.ExecuteNonQuery();
        }
        finally { if (opened) conn.Close(); }
    }

    private static bool? ReadAutoMigrateSetting(LlmRouterDbContext db)
    {
        try
        {
            // Raw read — the settings table may not exist on a fresh DB.
            var conn = db.Database.GetDbConnection();
            var opened = conn.State != System.Data.ConnectionState.Open;
            if (opened) conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Data FROM settings LIMIT 1";
                var data = cmd.ExecuteScalar() as string;
                if (string.IsNullOrEmpty(data)) return null;
                var sd = System.Text.Json.JsonDocument.Parse(data).RootElement;
                if (sd.ValueKind == System.Text.Json.JsonValueKind.Object
                    && sd.TryGetProperty("database", out var dsec)
                    && dsec.TryGetProperty("autoMigrate", out var am)
                    && am.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                    return am.GetBoolean();
                return null;
            }
            finally { if (opened) conn.Close(); }
        }
        catch { return null; }
    }

    /// <summary>
    /// Legacy idempotent DDL for databases older than the baseline — columns
    /// and tables added lazily across SPECs. All statements are safe no-ops on
    /// a migrated schema; new schema changes MUST go through real migrations.
    /// </summary>
    private static void LegacyBackfill(LlmRouterDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            foreach (var ddl in LegacyDdl)
            {
                cmd.CommandText = ddl;
                try { cmd.ExecuteNonQuery(); } catch { /* exists */ }
            }
        }
        finally { if (opened) conn.Close(); }
    }

    private static readonly string[] LegacyDdl =
    [
        "ALTER TABLE usageHistory ADD COLUMN LatencyMs INTEGER NOT NULL DEFAULT 0",
        // SPEC-082: combo invariants + composite tiers columns
        "ALTER TABLE combos ADD COLUMN AllowedProviders TEXT NULL",
        "ALTER TABLE combos ADD COLUMN AllowedFamilies TEXT NULL",
        "ALTER TABLE combos ADD COLUMN Tiers TEXT NULL",
        // SPEC-093: provider-reported caps persisted on syncedModels
        "ALTER TABLE syncedModels ADD COLUMN MaxOutputTokens INTEGER NULL",
        "ALTER TABLE syncedModels ADD COLUMN Vision INTEGER NULL",
        "ALTER TABLE syncedModels ADD COLUMN Tools INTEGER NULL",
        "ALTER TABLE syncedModels ADD COLUMN Reasoning INTEGER NULL",
        "ALTER TABLE syncedModels ADD COLUMN ReasoningEfforts TEXT NULL",
        // SPEC-034+: tables introduced via lazy DDL before migrations existed
        """CREATE TABLE IF NOT EXISTS compressionCombos (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Description TEXT NULL, Pipeline TEXT NOT NULL DEFAULT '[]', LanguagePacks TEXT NOT NULL DEFAULT '[]', OutputMode INTEGER NOT NULL DEFAULT 0, OutputModeIntensity TEXT NULL, IsDefault INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)""",
        """CREATE TABLE IF NOT EXISTS compressionComboAssignments (Id TEXT NOT NULL PRIMARY KEY, CompressionComboId TEXT NOT NULL, RoutingComboId TEXT NOT NULL, CreatedAt TEXT NOT NULL)""",
        """CREATE TABLE IF NOT EXISTS compressionRuns (Id TEXT NOT NULL PRIMARY KEY, Timestamp TEXT NOT NULL, PrincipalId TEXT NULL, Model TEXT NULL, EngineId TEXT NOT NULL, BeforeChars INTEGER NOT NULL, AfterChars INTEGER NOT NULL, ComboId TEXT NULL)""",
        """CREATE TABLE IF NOT EXISTS discoveryResults (Id TEXT NOT NULL PRIMARY KEY, ProviderId TEXT NOT NULL, Method TEXT NOT NULL, Endpoint TEXT NULL, AuthType TEXT NOT NULL DEFAULT 'none', Models TEXT NOT NULL DEFAULT '[]', RateLimit TEXT NULL, Feasibility INTEGER NOT NULL DEFAULT 3, RiskLevel TEXT NOT NULL DEFAULT 'none', Status TEXT NOT NULL DEFAULT 'pending', Notes TEXT NULL, DiscoveredAt TEXT NOT NULL, VerifiedAt TEXT NULL)""",
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
        """CREATE TABLE IF NOT EXISTS evalCases (Id TEXT NOT NULL PRIMARY KEY, SuiteId TEXT NOT NULL, Input TEXT NOT NULL, ExpectType TEXT NOT NULL DEFAULT 'contains', ExpectValue TEXT NOT NULL, Weight REAL NOT NULL DEFAULT 1, Ord INTEGER NOT NULL DEFAULT 0)""",
        """CREATE TABLE IF NOT EXISTS evalRuns (Id TEXT NOT NULL PRIMARY KEY, SuiteId TEXT NOT NULL, Target TEXT NOT NULL, Status TEXT NOT NULL DEFAULT 'running', Score REAL NOT NULL DEFAULT 0, Results TEXT NOT NULL DEFAULT '[]', StartedAt TEXT NOT NULL, DurationMs INTEGER NOT NULL DEFAULT 0)""",
        """CREATE TABLE IF NOT EXISTS a2aTasks (Id TEXT NOT NULL PRIMARY KEY, Agent TEXT NULL, Payload TEXT NOT NULL DEFAULT '{}', State TEXT NOT NULL DEFAULT 'queued', Result TEXT NULL, Error TEXT NULL, CreatedAt TEXT NOT NULL, StartedAt TEXT NULL, FinishedAt TEXT NULL)""",
        """CREATE TABLE IF NOT EXISTS conductorTasks (Id TEXT NOT NULL PRIMARY KEY, Goal TEXT NOT NULL, Model TEXT NULL, Steps TEXT NOT NULL DEFAULT '[]', State TEXT NOT NULL DEFAULT 'queued', Result TEXT NULL, Error TEXT NULL, CreatedAt TEXT NOT NULL, FinishedAt TEXT NULL)""",
        """CREATE TABLE IF NOT EXISTS modelCooldowns (Id TEXT NOT NULL PRIMARY KEY, Provider TEXT NOT NULL, Model TEXT NOT NULL, Until TEXT NOT NULL, Reason TEXT NULL, CreatedAt TEXT NOT NULL)""",
        """CREATE TABLE IF NOT EXISTS fallbackChains (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Steps TEXT NOT NULL DEFAULT '[]', Active INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL)""",
        """CREATE TABLE IF NOT EXISTS capabilityOverrides (Id TEXT NOT NULL PRIMARY KEY, Provider TEXT NOT NULL, Model TEXT NOT NULL, Capabilities TEXT NOT NULL DEFAULT '{}', UpdatedAt TEXT NOT NULL)""",
        """CREATE TABLE IF NOT EXISTS syncedModels (Id TEXT NOT NULL PRIMARY KEY, Provider TEXT NOT NULL, Model TEXT NOT NULL, Available INTEGER NOT NULL DEFAULT 1, LastSyncAt TEXT NOT NULL)""",
        """CREATE TABLE IF NOT EXISTS webhooks (Id TEXT NOT NULL PRIMARY KEY, Data TEXT NOT NULL DEFAULT '{}', CreatedAt TEXT NOT NULL)""",
        """CREATE TABLE IF NOT EXISTS webhookDeliveries (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, WebhookId TEXT NOT NULL, Data TEXT NOT NULL DEFAULT '{}', At TEXT NOT NULL)""",
        """CREATE TABLE IF NOT EXISTS quotaWindows (Id TEXT NOT NULL PRIMARY KEY, Provider TEXT NOT NULL, Name TEXT NOT NULL, WindowMinutes INTEGER NOT NULL, MaxTokens INTEGER NOT NULL, MaxRequests INTEGER NOT NULL, CreatedAt TEXT NOT NULL)""",
        """CREATE TABLE IF NOT EXISTS credentialExpirations (Id TEXT NOT NULL PRIMARY KEY, ConnectionId TEXT NOT NULL, ExpiresAt TEXT NOT NULL, WarnDays INTEGER NOT NULL DEFAULT 7)""",
        """CREATE TABLE IF NOT EXISTS tags (Id TEXT NOT NULL PRIMARY KEY, TargetType TEXT NOT NULL, TargetId TEXT NOT NULL, Value TEXT NOT NULL)""",
        """CREATE TABLE IF NOT EXISTS policies (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Priority INTEGER NOT NULL DEFAULT 0, Rule TEXT NOT NULL DEFAULT '{}', Enabled INTEGER NOT NULL DEFAULT 1)""",
        """CREATE TABLE IF NOT EXISTS chatSessions (Id TEXT NOT NULL PRIMARY KEY, KeyId TEXT NOT NULL, Model TEXT NOT NULL, StartedAt TEXT NOT NULL, LastSeenAt TEXT NOT NULL, MessageCount INTEGER NOT NULL DEFAULT 0)""",
        """CREATE INDEX IF NOT EXISTS IX_usageHistory_ApiKey ON usageHistory (ApiKey)""",
        """CREATE INDEX IF NOT EXISTS IX_chatSessions_KeyId_Model ON chatSessions (KeyId, Model)""",
        """CREATE INDEX IF NOT EXISTS IX_providerConnections_Provider_IsActive ON providerConnections (Provider, IsActive)""",
        """CREATE INDEX IF NOT EXISTS IX_quotaWindows_Provider ON quotaWindows (Provider)""",
    ];
}
