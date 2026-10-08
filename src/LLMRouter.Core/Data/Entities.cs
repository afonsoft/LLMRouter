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
