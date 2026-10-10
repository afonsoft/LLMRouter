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
    /// <summary>SPEC-082: JSON array restricting step providers (invariants).</summary>
    public string? AllowedProviders { get; set; }
    /// <summary>SPEC-082: JSON array restricting step model families (invariants).</summary>
    public string? AllowedFamilies { get; set; }
    /// <summary>SPEC-082: JSON array of composite tier definitions.</summary>
    public string? Tiers { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

// ---- SPEC-085: provider discovery results ----

[Table("discoveryResults")]
public class DiscoveryResult
{
    [Key]
    public string Id { get; set; } = "";
    public string ProviderId { get; set; } = "";
    /// <summary>free_tier | web_cookie | auto_register | trial | public_api</summary>
    public string Method { get; set; } = "";
    public string? Endpoint { get; set; }
    /// <summary>none | cookie | api_key | oauth</summary>
    public string AuthType { get; set; } = "none";
    public string Models { get; set; } = "[]";
    public string? RateLimit { get; set; }
    public int Feasibility { get; set; } = 3; // 1-5
    /// <summary>none | low | medium | high | critical</summary>
    public string RiskLevel { get; set; } = "none";
    /// <summary>pending | testing | verified | rejected</summary>
    public string Status { get; set; } = "pending";
    public string? Notes { get; set; }
    public string DiscoveredAt { get; set; } = "";
    public string? VerifiedAt { get; set; }
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

// ---- SPEC-042: log export destinations ----

[Table("logExportDestinations")]
public class LogExportDestination
{
    [Key]
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";        // webhook | file | s3-compatible
    // {url?, path?, fmt?(jsonl|csv), scheduleMinutes?, headers?{}}
    public string Config { get; set; } = "{}";
    // {provider?, model?, status?, since?(ISO)}
    public string Filters { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
    public string? LastRunAt { get; set; }
    public string? LastRunStatus { get; set; }  // ok | error
    public string? LastRunDetail { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

// ---- SPEC-043: playground presets ----

[Table("playgroundPresets")]
public class PlaygroundPreset
{
    [Key]
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    // {system?, temperature?, maxTokens?, stream?}
    public string ParamsJson { get; set; } = "{}";
    public string CreatedAt { get; set; } = "";
}

// ---- SPEC-051: VSCode tokens ----

[Table("vscodeTokens")]
public class VscodeToken
{
    [Key]
    public string Id { get; set; } = "";
    public string Token { get; set; } = "";
    public string? Name { get; set; }
    public string? DefaultCombo { get; set; }
    // JSON array of combo names the token may use (empty = all)
    public string AllowedCombos { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
    public string CreatedAt { get; set; } = "";
}

// ---- SPEC-053: A2A tasks + conductor lifecycle ----

[Table("a2aTasks")]
public class A2aTask
{
    [Key]
    public string Id { get; set; } = "";
    public string? Agent { get; set; }
    // JSON: {model, text}
    public string Payload { get; set; } = "{}";
    // queued | running | done | failed | cancelled
    public string State { get; set; } = "queued";
    public string? Result { get; set; }
    public string? Error { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? StartedAt { get; set; }
    public string? FinishedAt { get; set; }
}

[Table("conductorTasks")]
public class ConductorTask
{
    [Key]
    public string Id { get; set; } = "";
    public string Goal { get; set; } = "";
    public string? Model { get; set; }
    // decomposed steps JSON
    public string Steps { get; set; } = "[]";
    // queued | running | done | failed | cancelled
    public string State { get; set; } = "queued";
    public string? Result { get; set; }
    public string? Error { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? FinishedAt { get; set; }
}

// ---- SPEC-052: evals ----

[Table("evalSuites")]
public class EvalSuite
{
    [Key]
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

[Table("evalCases")]
public class EvalCase
{
    [Key]
    public string Id { get; set; } = "";
    public string SuiteId { get; set; } = "";
    public string Input { get; set; } = "";
    // contains | regex | json | judge
    public string ExpectType { get; set; } = "contains";
    public string ExpectValue { get; set; } = "";
    public double Weight { get; set; } = 1;
    public int Ord { get; set; }
}

[Table("evalRuns")]
public class EvalRun
{
    [Key]
    public string Id { get; set; } = "";
    public string SuiteId { get; set; } = "";
    public string Target { get; set; } = "";
    // running | done | failed
    public string Status { get; set; } = "running";
    public double Score { get; set; }
    // JSON array of per-case verdicts
    public string Results { get; set; } = "[]";
    public string StartedAt { get; set; } = "";
    public long DurationMs { get; set; }
}

// ---- SPEC-050: CLI device login ----

[Table("cliTokens")]
public class CliToken
{
    [Key]
    public string Id { get; set; } = "";
    // 12-char code the CLI shows/polls with
    public string Token { get; set; } = "";
    // pending | approved | revoked
    public string State { get; set; } = "pending";
    public string? DeviceName { get; set; }
    // apiKeys.Key minted on approve
    public string? ApiKey { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? ApprovedAt { get; set; }
}

// ---- SPEC-049: session pools ----

[Table("sessionPools")]
public class SessionPoolRow
{
    [Key]
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    // "round-robin" | "least-used"
    public string Strategy { get; set; } = "round-robin";
    public int MinSize { get; set; } = 1;
    public int MaxSize { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 60;
    public bool IsActive { get; set; } = true;
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

[Table("poolSessions")]
public class PoolSession
{
    [Key]
    public string Id { get; set; } = "";
    public string PoolId { get; set; } = "";
    public string? ConnectionId { get; set; }
    // idle | busy | cooldown | dead
    public string State { get; set; } = "idle";
    // healthy | degraded | dead
    public string Health { get; set; } = "healthy";
    public string? BusyUntil { get; set; }
    public string? CooldownUntil { get; set; }
    public string? LastUsedAt { get; set; }
    public long TotalRequests { get; set; }
    public long SuccessfulRequests { get; set; }
    public int ConsecutiveFails { get; set; }
    public string CreatedAt { get; set; } = "";
}

// ---- SPEC-048: relay tokens ----

[Table("relayTokens")]
public class RelayToken
{
    [Key]
    public string Id { get; set; } = "";
    public string Token { get; set; } = "";
    public string? Name { get; set; }
    // JSON array of allowed "provider/model" strings (empty = all); supports trailing '*'
    public string AllowedModels { get; set; } = "[]";
    // requests/day; 0 = unlimited
    public int QuotaRequests { get; set; }
    // tokens/day (prompt+completion); 0 = unlimited
    public long QuotaTokens { get; set; }
    public string? ExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;
    public string CreatedAt { get; set; } = "";
}

// ---- SPEC-045: prompt cache ----

[Table("cacheEntries")]
public class CacheEntry
{
    [Key]
    public string Id { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Request { get; set; } = "{}";
    public string Response { get; set; } = "{}";
    // 1 when the request carried a reasoning/thinking block
    public int Reasoning { get; set; }
    public int TokensSaved { get; set; }
    public int Hits { get; set; }
    public string CreatedAt { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
}

// ---- SPEC-068: core gateway gaps (scaffold — impls in parallel specs) ----

[Table("modelCooldowns")]
public class ModelCooldown
{
    [Key]
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Until { get; set; } = "";
    public string? Reason { get; set; }
    public string CreatedAt { get; set; } = "";
}

[Table("fallbackChains")]
public class FallbackChain
{
    [Key]
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    // JSON array of steps [{combo|provider/model, retries, timeoutMs}]
    public string Steps { get; set; } = "[]";
    public bool Active { get; set; } = true;
    public string CreatedAt { get; set; } = "";
}

[Table("capabilityOverrides")]
public class CapabilityOverride
{
    [Key]
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    // JSON {vision,tools,json,streaming,reasoning,...}
    public string Capabilities { get; set; } = "{}";
    public string UpdatedAt { get; set; } = "";
}

[Table("syncedModels")]
public class SyncedModel
{
    [Key]
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public bool Available { get; set; } = true;
    public string LastSyncAt { get; set; } = "";
}

[Table("quotaWindows")]
public class QuotaWindow
{
    [Key]
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Name { get; set; } = "";
    public int WindowMinutes { get; set; }
    public long MaxTokens { get; set; }
    public long MaxRequests { get; set; }
    public string CreatedAt { get; set; } = "";
}

[Table("credentialExpirations")]
public class CredentialExpiration
{
    [Key]
    public string Id { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public int WarnDays { get; set; } = 7;
}

[Table("tags")]
public class Tag
{
    [Key]
    public string Id { get; set; } = "";
    // "model" | "provider" | "combo" | "key"
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Value { get; set; } = "";
}

[Table("policies")]
public class Policy
{
    [Key]
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Priority { get; set; }
    // JSON {match:{provider,model,key,tag}, action:{block|route-to|require-tag|limit}}
    public string Rule { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
}

[Table("chatSessions")]
public class ChatSession
{
    [Key]
    public string Id { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string Model { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string LastSeenAt { get; set; } = "";
    public int MessageCount { get; set; }
}

/// <summary>SPEC-057: evento de auditoria estruturado {actor,action,target,meta,ip,at}.</summary>
[Table("auditEvents")]
public class AuditEvent
{
    [Key]
    public long Id { get; set; }
    /// <summary>Quem agiu: "dashboard", "apiKey:sk-a…", "system" ou null.</summary>
    public string? Actor { get; set; }
    /// <summary>Ação no formato dominio.verbo (ex.: auth.login, apikey.reveal).</summary>
    public string Action { get; set; } = "";
    /// <summary>Alvo afetado (id/nome do recurso), quando houver.</summary>
    public string? Target { get; set; }
    /// <summary>Metadados JSON opcionais.</summary>
    public string? Meta { get; set; }
    /// <summary>IP remoto do chamador, quando houver.</summary>
    public string? Ip { get; set; }
    /// <summary>Timestamp UTC (ISO 8601 "O").</summary>
    public string At { get; set; } = "";
}

/// <summary>SPEC-057: trilha de chamadas de ferramentas MCP (proxy /mcp).</summary>
[Table("mcpToolCalls")]
public class McpToolCall
{
    [Key]
    public long Id { get; set; }
    /// <summary>Servidor MCP alvo ("builtin" ou nome do upstream).</summary>
    public string? Server { get; set; }
    /// <summary>Nome da ferramenta chamada.</summary>
    public string Tool { get; set; } = "";
    /// <summary>SHA-256 dos argumentos (não guarda payload — pode conter segredos).</summary>
    public string? ArgsHash { get; set; }
    public long DurationMs { get; set; }
    public bool Ok { get; set; }
    /// <summary>Timestamp UTC (ISO 8601 "O").</summary>
    public string At { get; set; } = "";
}

/// <summary>SPEC-058: histórico de execuções de skills (command declarado).</summary>
[Table("skillExecutions")]
public class SkillExecution
{
    [Key]
    public long Id { get; set; }
    /// <summary>Id (dir) da skill executada.</summary>
    public string Skill { get; set; } = "";
    /// <summary>Comando declarado executado.</summary>
    public string Command { get; set; } = "";
    public int ExitCode { get; set; }
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public long DurationMs { get; set; }
    /// <summary>Timestamp UTC (ISO 8601 "O").</summary>
    public string At { get; set; } = "";
}


/// <summary>SPEC-055: itens do radar (kind: catalog|offer|intel|referral).</summary>
[Table("radarItems")]
public class RadarItem
{
    [Key]
    public long Id { get; set; }
    /// <summary>Tipo do item: catalog, offer, intel, referral.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Chave única dentro do kind (slug/url).</summary>
    public string ItemKey { get; set; } = "";
    public string? Title { get; set; }
    /// <summary>Payload JSON do item.</summary>
    public string? Data { get; set; }
    /// <summary>Timestamp UTC (ISO 8601 "O").</summary>
    public string At { get; set; } = "";
}

/// <summary>SPEC-056: itens de gamification (kind: earned|invite|notification|server|transfer|anomaly|scoreEvent).</summary>
[Table("gamiItems")]
public class GamiItem
{
    [Key]
    public long Id { get; set; }
    /// <summary>Tipo: earned, invite, notification, server, transfer, anomaly, scoreEvent.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Chave (badge id, invite code, server id…).</summary>
    public string ItemKey { get; set; } = "";
    /// <summary>Ator (api key prefix / dashboard).</summary>
    public string? Actor { get; set; }
    /// <summary>Pontos associados (scoreEvent/transfer).</summary>
    public long Points { get; set; }
    /// <summary>Payload JSON.</summary>
    public string? Data { get; set; }
    /// <summary>Timestamp UTC (ISO 8601 "O").</summary>
    public string At { get; set; } = "";
}

/// <summary>SPEC-065: execuções do issue-agent (github|slack → fix flow).</summary>
[Table("issueAgentRuns")]
public class IssueAgentRun
{
    [Key]
    public long Id { get; set; }
    /// <summary>Origem: github, slack, manual.</summary>
    public string Source { get; set; } = "";
    /// <summary>Issue/URL alvo.</summary>
    public string Issue { get; set; } = "";
    /// <summary>Estado: queued, running, done, failed.</summary>
    public string State { get; set; } = "queued";
    /// <summary>PR gerado (se houver).</summary>
    public string? PrUrl { get; set; }
    /// <summary>Detalhe/último log.</summary>
    public string? Detail { get; set; }
    /// <summary>Timestamp UTC (ISO 8601 "O").</summary>
    public string At { get; set; } = "";
}
