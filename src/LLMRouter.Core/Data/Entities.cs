using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Data;

/// <summary>
/// Entities mirror the upstream OmniRoute/9router SQLite schema
/// (src/lib/db/schema.js) one-to-one so data is portable.
/// </summary>
[Table("_meta")]
public class MetaEntry
{
    [Key]
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

[Table("settings")]
public class SettingRow
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = 1;
    public string Data { get; set; } = "{}";
}

[Table("providerConnections")]
public class ProviderConnection
{
    [Key]
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string AuthType { get; set; } = "";
    public string? Name { get; set; }
    public string? Email { get; set; }
    public int? Priority { get; set; }
    public bool IsActive { get; set; } = true;
    public string Data { get; set; } = "{}";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

[Table("providerNodes")]
public class ProviderNode
{
    [Key]
    public string Id { get; set; } = "";
    public string? Type { get; set; }
    public string? Name { get; set; }
    public string Data { get; set; } = "{}";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

[Table("proxyPools")]
public class ProxyPool
{
    [Key]
    public string Id { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string? TestStatus { get; set; }
    public string Data { get; set; } = "{}";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

[Table("apiKeys")]
public class ApiKey
{
    [Key]
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string? Name { get; set; }
    public string? MachineId { get; set; }
    public bool IsActive { get; set; } = true;
    public string CreatedAt { get; set; } = "";
    public bool AccessRestricted { get; set; }
    public string? AccessAllow { get; set; }
}

[Table("combos")]
public class Combo
{
    [Key]
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Kind { get; set; }
    /// <summary>Round-robin: consecutive requests served by the same model before rotating.</summary>
    public int StickyLimit { get; set; } = 1;
    /// <summary>JSON array of "provider/model" strings.</summary>
    public string Models { get; set; } = "[]";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

[Table("kv")]
public class KvEntry
{
    public string Scope { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

[Table("usageHistory")]
public class UsageRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public string Timestamp { get; set; } = "";
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? ConnectionId { get; set; }
    public string? ApiKey { get; set; }
    public string? Endpoint { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public double Cost { get; set; }
    public long LatencyMs { get; set; }
    public string? Status { get; set; }
    public string? Tokens { get; set; }
    public string? Meta { get; set; }
}

[Table("usageDaily")]
public class UsageDaily
{
    [Key]
    public string DateKey { get; set; } = "";
    public string Data { get; set; } = "{}";
}

[Table("requestDetails")]
public class RequestDetail
{
    [Key]
    public string Id { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? ConnectionId { get; set; }
    public string? Status { get; set; }
    public string Data { get; set; } = "{}";
}

[Table("compressionCombos")]
public class CompressionCombo
{
    [Key]
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Pipeline { get; set; } = "[]";
    public string LanguagePacks { get; set; } = "[]";
    public bool OutputMode { get; set; }
    public string? OutputModeIntensity { get; set; }
    public bool IsDefault { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

[Table("compressionComboAssignments")]
public class CompressionComboAssignment
{
    [Key]
    public string Id { get; set; } = "";
    public string CompressionComboId { get; set; } = "";
    public string RoutingComboId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

[Table("compressionRuns")]
public class CompressionRun
{
    [Key]
    public string Id { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string? PrincipalId { get; set; }
    public string? Model { get; set; }
    public string EngineId { get; set; } = "";
    public long BeforeChars { get; set; }
    public long AfterChars { get; set; }
    public string? ComboId { get; set; }
}

[Table("jobStates")]
public class JobState
{
    [Key]
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string? LastRun { get; set; }
    public string? NextRun { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public long LastDurationMs { get; set; }
}

[Table("jobRuns")]
public class JobRun
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public string JobId { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public long DurationMs { get; set; }
    public string Status { get; set; } = "";
    public string? Output { get; set; }
}

[Table("rateLimits")]
public class RateLimit
{
    [Key]
    public string Id { get; set; } = "";
    public string Scope { get; set; } = "";      // apiKey | provider | model
    public string ScopeValue { get; set; } = ""; // "*" or value/prefix*
    public int Rpm { get; set; }                 // requests/min; 0 = unlimited
    public int Tpm { get; set; }                 // tokens/min; 0 = unlimited
    public int Burst { get; set; }               // extra request headroom over Rpm
    public bool Enabled { get; set; } = true;
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

[Table("files")]
public class FileEntry
{
    [Key]
    public string Id { get; set; } = "";
    public string Filename { get; set; } = "";
    public long Bytes { get; set; }
    public string Purpose { get; set; } = "";
    public string Mime { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

// ---- SPEC-041: advanced keys + quota ----

[Table("keyGroups")]
public class KeyGroup
{
    [Key]
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    // JSON array of apiKeys ids
    public string KeysJson { get; set; } = "[]";
    public string CreatedAt { get; set; } = "";
}

[Table("quotaPlans")]
public class QuotaPlan
{
    [Key]
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    // JSON: {rpm?, tpm?, dailyTokens?}
    public string LimitsJson { get; set; } = "{}";
    public double? Price { get; set; }
    public string CreatedAt { get; set; } = "";
}

[Table("quotaSchedules")]
public class QuotaSchedule
{
    [Key]
    public string Id { get; set; } = "";
    // "all" | an apiKeys id | a keyGroups id
    public string Target { get; set; } = "all";
    // "daily" | "weekly" | "monthly"
    public string Window { get; set; } = "daily";
    public string? LastRunAt { get; set; }
    public string CreatedAt { get; set; } = "";
}
