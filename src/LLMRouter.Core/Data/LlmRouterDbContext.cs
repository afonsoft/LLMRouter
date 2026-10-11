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
    public DbSet<DiscoveryResult> DiscoveryResults => Set<DiscoveryResult>();
    public DbSet<UsageRecord> UsageHistory => Set<UsageRecord>();
    public DbSet<UsageDaily> UsageDaily => Set<UsageDaily>();
    public DbSet<RequestDetail> RequestDetails => Set<RequestDetail>();
    /// <summary>SPEC-116: per-request proxy journal (upstream proxy_logs parity).</summary>
    public DbSet<ProxyLog> ProxyLogs => Set<ProxyLog>();
    /// <summary>SPEC-117: provider quota telemetry snapshots (upstream quota_snapshots).</summary>
    public DbSet<QuotaSnapshot> QuotaSnapshots => Set<QuotaSnapshot>();
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
    /// <summary>SPEC-057: eventos de auditoria estruturados.</summary>
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    /// <summary>SPEC-111: usuários finais (modelo OpenRouter/LiteLLM).</summary>
    public DbSet<User> Users => Set<User>();
    /// <summary>SPEC-111: virtual keys vk-… pertencentes a usuários.</summary>
    public DbSet<UserKey> UserKeys => Set<UserKey>();
    /// <summary>SPEC-111: ledger de crédito append-only por usuário.</summary>
    public DbSet<CreditLedgerRow> CreditLedger => Set<CreditLedgerRow>();
    /// <summary>SPEC-057: trilha de chamadas MCP.</summary>
    public DbSet<McpToolCall> McpToolCalls => Set<McpToolCall>();
    /// <summary>SPEC-058: histórico de execuções de skills.</summary>
    public DbSet<SkillExecution> SkillExecutions => Set<SkillExecution>();
    /// <summary>SPEC-055: itens do radar (catalog/offers/intel/referrals).</summary>
    public DbSet<RadarItem> RadarItems => Set<RadarItem>();
    /// <summary>SPEC-056: itens de gamification.</summary>
    public DbSet<GamiItem> GamiItems => Set<GamiItem>();
    /// <summary>SPEC-065: execuções do issue-agent.</summary>
    public DbSet<IssueAgentRun> IssueAgentRuns => Set<IssueAgentRun>();
    /// <summary>SPEC-098: webhooks (kv-blob partition).</summary>
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    /// <summary>SPEC-098: entregas de webhook (kv-blob partition).</summary>
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

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
        typeof(CreditLedgerRow), // SPEC-111: gravado por request (usage debit)
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

    /// <summary>
    /// Creates the schema (single-file embedded DB, mirroring the upstream bootstrap).
    /// SPEC-109: delegates to <see cref="SchemaMigrator"/> — EF migrations own the
    /// schema; the legacy idempotent DDL backfill still runs as a safety net for
    /// databases older than the baseline.
    /// </summary>
    public void EnsureCreated()
    {
        lock (EnsureCreatedLock)
        {
            SchemaMigrator.Apply(this, autoMigrate: true);
        }
    }

}
