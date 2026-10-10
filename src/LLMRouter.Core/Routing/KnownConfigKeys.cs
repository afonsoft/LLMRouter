namespace LLMRouter.Core.Routing;

/// <summary>
/// SPEC-081 — known top-level keys of settings.data (upstream deadConfigKeys.ts
/// parity). Keys outside this set are reported as dead by
/// /api/settings/dead-config-keys.
/// </summary>
public static class KnownConfigKeys
{
    public static readonly HashSet<string> Set = new(StringComparer.OrdinalIgnoreCase)
    {
        "adminPasswordHash", "autoRouter", "autoDisable", "comboAutoPruneStaleSteps",
        "comboAutoPromoteEnabled", "comboDefaults", "comboPresetsAutoSeed",
        "compression", "contextCompression", "freeProxies", "freeTier", "guardrails",
        "ipFilter", "jobs", "lastError", "lastTestAt", "memory", "mimeType",
        "monthlyBudget", "oneproxy", "payloadRules", "pricing", "promptCache",
        "rateLimitedUntil", "rateLimits", "reasoningRoutingRules", "requireLogin",
        "resilience", "retention", "rtk", "skillsInjection", "systemPrompt",
        "taskRouting", "testStatus", "thinkingBudget", "tiers", "tokenSaver",
    };
}
