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

    /// <summary>Creates the schema (single-file embedded DB, mirroring the upstream bootstrap).</summary>
    public void EnsureCreated()
    {
        Database.EnsureCreated();
        // Lightweight column migration for DBs created before the column existed.
        var conn = Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE usageHistory ADD COLUMN LatencyMs INTEGER NOT NULL DEFAULT 0";
            try { cmd.ExecuteNonQuery(); } catch { /* column already exists */ }
        }
        finally { if (opened) conn.Close(); }
    }
}
