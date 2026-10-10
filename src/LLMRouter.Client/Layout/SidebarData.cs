namespace LLMRouter.Client.Layout;

/// <summary>Sidebar nav model — verbatim port of upstream sections.ts (96 items, 10 sections).</summary>
public static class SidebarData
{
    public sealed record NavItem(string Id, string Href, string Key, string Icon, string? Label = null, string? Subtitle = null, string? SubtitleFallback = null, NavItem[]? Children = null);
    public sealed record NavGroup(string? Id, string? TitleKey, string? TitleFallback, NavItem[] Items);
    public sealed record NavSection(string Id, string TitleKey, string? TitleFallback, NavItem[]? Items = null, NavGroup[]? Groups = null);

    public static readonly NavSection[] Sections =
    [
        new("home", "home", "Home",
            Items: [
            new("home", "/home", "home", "home", Subtitle: "homeSubtitle")
            ]
        ),
        new("omni-proxy", "omniProxySection", "OmniProxy",
            Items: [
            new("endpoints", "/dashboard/endpoint", "endpoints", "api", Subtitle: "endpointsSubtitle"),
            new("api-manager", "/dashboard/api-manager", "apiManager", "vpn_key", Subtitle: "apiManagerSubtitle"),
            new("providers", "/dashboard/providers", "providers", "dns", Subtitle: "providersSubtitle"),
            new("model-catalog", "/dashboard/models", "modelCatalog", "view_list", Label: "Models & Combos", Subtitle: "modelCatalogSubtitle", SubtitleFallback: "Browse models and combos across providers"),
            new("embedded-services", "/dashboard/providers/services", "embeddedServices", "deployed_code", Subtitle: "embeddedServicesSubtitle"),
            new("combos", "/dashboard/combos", "combos", "layers", Subtitle: "combosSubtitle"),
            new("combos-live", "/dashboard/combos/live", "combosLive", "account_tree", Label: "Combo Studio", Subtitle: "combosLiveSubtitle", SubtitleFallback: "Live routing cascade"),
            new("quota", "/dashboard/quota", "providerQuota", "tune", Subtitle: "providerQuotaSubtitle"),
            new("rate-limits", "/dashboard/rate-limits", "rateLimits", "speed", Label: "Rate limits", Subtitle: "rateLimitsSubtitle", SubtitleFallback: "Per-key/provider/model throttling"),
            new("relay", "/dashboard/relay", "relay", "podcasts", Label: "Relay", Subtitle: "relaySubtitle", SubtitleFallback: "Per-client access tokens"),
            new("costs-quota-share", "/dashboard/costs/quota-share", "costsQuotaShare", "pie_chart", Subtitle: "costsQuotaShareSubtitle"),
            new("proxy", "/dashboard/system/proxy", "proxy", "dns", Subtitle: "proxySubtitle")
            ],
            Groups: [
                new("compression-context", "compressionContextGroup", "Compression Context", [
                    new("context-settings", "/dashboard/context/settings", "contextSettings", "settings", Label: "Compression Settings", Subtitle: "contextSettingsSubtitle", SubtitleFallback: "Global defaults"),
                    new("context-combos", "/dashboard/context/combos", "contextCombos", "hub", Subtitle: "contextCombosSubtitle"),
                    new("context-caveman", "/dashboard/context/caveman", "contextCaveman", "compress", Subtitle: "contextCavemanSubtitle"),
                    new("context-rtk", "/dashboard/context/rtk", "contextRtk", "filter_alt", Subtitle: "contextRtkSubtitle"),
                    new("context-headroom", "/dashboard/context/headroom", "contextHeadroom", "table_rows", Label: "Headroom", Subtitle: "contextHeadroomSubtitle", SubtitleFallback: "Tabular compaction"),
                    new("context-session-dedup", "/dashboard/context/session-dedup", "contextSessionDedup", "content_copy", Label: "Session Dedup", Subtitle: "contextSessionDedupSubtitle", SubtitleFallback: "Cross-turn dedup"),
                    new("context-ccr", "/dashboard/context/ccr", "contextCcr", "archive", Label: "CCR", Subtitle: "contextCcrSubtitle", SubtitleFallback: "Retrieve markers"),
                    new("context-llmlingua", "/dashboard/context/llmlingua", "contextLlmlingua", "psychology", Label: "LLMLingua", Subtitle: "contextLlmlinguaSubtitle", SubtitleFallback: "Semantic pruning"),
                    new("context-lite", "/dashboard/context/lite", "contextLite", "compress", Label: "Lite", Subtitle: "contextLiteSubtitle", SubtitleFallback: "Fast whitespace cleanup"),
                    new("context-aggressive", "/dashboard/context/aggressive", "contextAggressive", "speed", Label: "Aggressive", Subtitle: "contextAggressiveSubtitle", SubtitleFallback: "Summary + aging"),
                    new("context-ultra", "/dashboard/context/ultra", "contextUltra", "bolt", Label: "Ultra", Subtitle: "contextUltraSubtitle", SubtitleFallback: "Heuristic pruning"),
                    new("context-omniglyph", "/dashboard/context/omniglyph", "contextOmniglyph", "grain", Label: "OmniGlyph", Subtitle: "contextOmniglyphSubtitle", SubtitleFallback: "Context-as-image"),
                    new("compression-studio", "/dashboard/compression/studio", "compressionStudio", "monitoring", Label: "Compression Studio", Subtitle: "compressionStudioSubtitle", SubtitleFallback: "Live engine cascade"),
                    new("compression-exclusions", "/dashboard/compression/exclusions", "compressionExclusions", "block", Label: "Exclusions", Subtitle: "compressionExclusionsSubtitle", SubtitleFallback: "Per-model/endpoint bypass")
                ]),
                new("tools", "toolsGroup", "Tools", [
                    new("cli-code", "/dashboard/cli-code", "cliCode", "terminal", Subtitle: "cliCodeSubtitle"),
                    new("cli-agents", "/dashboard/cli-agents", "cliAgents", "smart_toy", Subtitle: "cliAgentsSubtitle"),
                    new("acp-agents", "/dashboard/acp-agents", "acpAgents", "device_hub", Subtitle: "acpAgentsSubtitle"),
                    new("cloud-agents", "/dashboard/cloud-agents", "cloudAgents", "cloud", Subtitle: "cloudAgentsSubtitle"),
                    new("conductor", "/dashboard/conductor", "conductor", "account_tree", Label: "Conductor", Subtitle: "conductorSubtitle", SubtitleFallback: "CLI-agent fleet"),
                    new("orchestration", "/dashboard/orchestration", "orchestration", "account_tree", Subtitle: "orchestrationSubtitle"),
                    new("agent-bridge", "/dashboard/tools/agent-bridge", "agentBridge", "link", Subtitle: "agentBridgeSubtitle"),
                    new("traffic-inspector", "/dashboard/tools/traffic-inspector", "trafficInspector", "network_check", Subtitle: "trafficInspectorSubtitle"),
                    new("discovery", "/dashboard/discovery", "discovery", "travel_explore", Subtitle: "discoverySubtitle")
                ]),
                new("integrations", "integrationsGroup", "Integrations", [
                    new("api-endpoints", "/dashboard/api-endpoints", "apiEndpoints", "api", Subtitle: "apiEndpointsSubtitle"),
                    new("webhooks", "/dashboard/webhooks", "webhooks", "webhook", Subtitle: "webhooksSubtitle"),
                    new("log-export", "/dashboard/log-export", "logExport", "cloud_upload", Label: "Log export", Subtitle: "logExportSubtitle", SubtitleFallback: "Ship call logs out"),
                    new("jobs", "/dashboard/jobs", "jobs", "schedule", Label: "Jobs", Subtitle: "jobsSubtitle", SubtitleFallback: "Scheduled tasks")
                ])
            ]
        ),
        new("analytics", "analyticsSection", "Analytics",
            Items: [
            new("analytics", "/dashboard/analytics", "usage", "analytics", Subtitle: "usageSubtitle"),
            new("analytics-combo-health", "/dashboard/analytics/combo-health", "analyticsComboHealth", "monitor_heart", Subtitle: "analyticsComboHealthSubtitle"),
            new("analytics-utilization", "/dashboard/analytics/utilization", "analyticsUtilization", "bar_chart", Subtitle: "analyticsUtilizationSubtitle"),
            new("cache", "/dashboard/cache", "cache", "cached", Subtitle: "cacheSubtitle"),
            new("analytics-compression", "/dashboard/analytics/compression", "analyticsCompression", "compress", Subtitle: "analyticsCompressionSubtitle"),
            new("analytics-search", "/dashboard/analytics/search", "analyticsSearch", "manage_search", Subtitle: "analyticsSearchSubtitle"),
            new("analytics-evals", "/dashboard/analytics/evals", "analyticsEvals", "labs", Subtitle: "analyticsEvalsSubtitle"),
            new("provider-stats", "/dashboard/provider-stats", "providerStats", "speed", Subtitle: "providerStatsSubtitle")
            ]
        ),
        new("costs", "costsSection", "Costs",
            Items: [
            new("costs", "/dashboard/costs", "costsOverview", "account_balance_wallet", Subtitle: "costsOverviewSubtitle"),
            new("costs-pricing", "/dashboard/costs/pricing", "costsPricing", "price_change", Subtitle: "costsPricingSubtitle"),
            new("costs-budget", "/dashboard/costs/budget", "costsBudget", "savings", Subtitle: "costsBudgetSubtitle"),
            new("costs-free-tiers", "/dashboard/free-tiers", "costsFreeTiers", "request_quote", Subtitle: "costsFreeTiersSubtitle"),
            new("free-provider-rankings", "/dashboard/free-provider-rankings", "freeProviderRankings", "leaderboard", Subtitle: "freeProviderRankingsSubtitle"),
            new("radar", "/dashboard/radar", "radar", "radar", Subtitle: "radarSubtitle")
            ]
        ),
        new("monitoring", "monitoringSection", "Monitoring",
            Items: [
            new("activity", "/dashboard/activity", "activity", "timeline", Subtitle: "activitySubtitle")
            ],
            Groups: [
                new("logs", "logsGroup", "Logs", [
                    new("logs", "/dashboard/logs", "logs", "description", Subtitle: "logsSubtitle"),
                    new("logs-proxy", "/dashboard/logs/proxy", "logsProxy", "lan", Subtitle: "logsProxySubtitle"),
                    new("logs-console", "/dashboard/logs/console", "consoleLogs", "terminal", Subtitle: "consoleLogsSubtitle"),
                    new("logs-timeline", "/dashboard/logs/timeline", "logsTimeline", "view_timeline", Subtitle: "logsTimelineSubtitle"),
                    new("conversations", "/dashboard/conversations", "conversations", "forum", Subtitle: "conversationsSubtitle")
                ]),
                new("audit", "auditGroup", "Audit", [
                    new("audit", "/dashboard/audit", "auditLog", "policy", Subtitle: "auditLogSubtitle"),
                    new("audit-mcp", "/dashboard/audit/mcp", "auditMcp", "security", Subtitle: "auditMcpSubtitle"),
                    new("audit-a2a", "/dashboard/audit/a2a", "auditA2a", "device_hub", Subtitle: "auditA2aSubtitle")
                ]),
                new("system", "systemGroup", "System", [
                    new("health", "/dashboard/health", "health", "health_and_safety", Subtitle: "healthSubtitle"),
                    new("runtime", "/dashboard/runtime", "runtime", "bolt", Subtitle: "runtimeSubtitle"),
                    new("resilience-connections", "/dashboard/resilience/connections", "resilienceConnections", "shield", Subtitle: "resilienceConnectionsSubtitle"),
                    new("resilience-cooldowns", "/dashboard/resilience/cooldowns", "resilienceCooldowns", "timer_off", Subtitle: "resilienceCooldownsSubtitle")
                ])
            ]
        ),
        new("devtools", "devtoolsSection", "Dev Tools",
            Items: [
            new("translator", "/dashboard/translator", "translator", "translate", Subtitle: "translatorSubtitle"),
            new("playground", "/dashboard/playground", "playground", "science", Subtitle: "playgroundSubtitle"),
            new("search-tools", "/dashboard/search-tools", "searchTools", "manage_search", Subtitle: "searchToolsSubtitle")
            ]
        ),
        new("agentic-features", "agenticFeaturesSection", "Agentic Features",
            Items: [
            new("memory", "/dashboard/memory", "memory", "psychology", Subtitle: "memorySubtitle"),
            new("agent-skills", "/dashboard/agent-skills", "agentSkills", "share", Subtitle: "agentSkillsSubtitle"),
            new("chaos-config", "/dashboard/chaos", "chaosConfig", "blender", Label: "Chaos Mode", Subtitle: "chaosConfigSubtitle", SubtitleFallback: "Multi-model parallel execution"),
            new("skills", "/dashboard/omni-skills", "omniSkills", "auto_fix_high", Subtitle: "omniSkillsSubtitle"),
            new("mcp", "/dashboard/mcp", "mcp", "hub", Subtitle: "mcpSubtitle"),
            new("a2a", "/dashboard/a2a", "a2a", "device_hub", Subtitle: "a2aSubtitle"),
            new("plugins", "/dashboard/plugins", "plugins", "extension", Subtitle: "pluginsSubtitle")
            ]
        ),
        new("other-features", "otherFeaturesSection", "Other Features",
            Items: [
            new("media", "/dashboard/cache/media", "media", "perm_media", Subtitle: "mediaSubtitle")
            ],
            Groups: [
                new("gamification", "gamificationGroup", "Gamification", [
                    new("leaderboard", "/dashboard/leaderboard", "leaderboard", "emoji_events", Subtitle: "leaderboardSubtitle"),
                    new("profile", "/dashboard/profile", "profile", "person", Subtitle: "profileSubtitle"),
                    new("tokens", "/dashboard/tokens", "tokens", "toll", Subtitle: "tokensSubtitle"),
                    new("gamification-admin", "/dashboard/gamification/admin", "gamificationAdmin", "admin_panel_settings", Subtitle: "gamificationAdminSubtitle")
                ]),
                new("batch", "batchGroup", "Batch", [
                    new("batch", "/dashboard/batch", "batch", "view_list", Subtitle: "batchSubtitle"),
                    new("batch-files", "/dashboard/batch/files", "batchFiles", "folder", Subtitle: "batchFilesSubtitle")
                ])
            ]
        ),
        new("configuration", "configurationSection", "Configuration",
            Items: [
            new("settings-general", "/dashboard/settings/general", "settingsGeneral", "tune", Subtitle: "settingsGeneralSubtitle"),
            new("settings-appearance", "/dashboard/settings/appearance", "settingsAppearance", "palette", Subtitle: "settingsAppearanceSubtitle"),
            new("settings-ai", "/dashboard/settings/ai", "settingsAi", "auto_awesome", Subtitle: "settingsAiSubtitle"),
            new("settings-modality-bridge", "/dashboard/settings/modality-bridge", "settingsModalityBridge", "image_search", Subtitle: "settingsModalityBridgeSubtitle"),
            new("settings-routing", "/dashboard/settings/routing", "globalRouting", "route", Subtitle: "globalRoutingSubtitle"),
            new("settings-resilience", "/dashboard/settings/resilience", "settingsResilience", "health_and_safety", Subtitle: "settingsResilienceSubtitle"),
            new("settings-advanced", "/dashboard/settings/advanced", "settingsAdvanced", "engineering", Subtitle: "settingsAdvancedSubtitle"),
            new("settings-security", "/dashboard/settings/security", "settingsSecurity", "shield", Subtitle: "settingsSecuritySubtitle"),
            new("settings-access-tokens", "/dashboard/settings/access-tokens", "settingsAccessTokens", "key", Label: "Access Tokens", Subtitle: "settingsAccessTokensSubtitle"),
            new("settings-feature-flags", "/dashboard/settings/feature-flags", "settingsFeatureFlags", "flag", Subtitle: "settingsFeatureFlagsSubtitle"),
            new("settings-cache", "/dashboard/settings/cache", "settingsCache", "memory", Subtitle: "settingsCacheSubtitle"),
            new("settings-sidebar", "/dashboard/settings/sidebar", "settingsSidebar", "view_sidebar", Subtitle: "settingsSidebarSubtitle")
            ]
        ),
        new("help", "helpSection", "Help",
            Items: [
            new("docs", "/docs", "docs", "menu_book", Subtitle: "docsSubtitle"),
            new("issues", "https://github.com/diegosouzapw/OmniRoute/issues", "issues", "bug_report", Subtitle: "issuesSubtitle"),
            new("changelog", "/dashboard/changelog", "changelog", "campaign", Subtitle: "changelogSubtitle")
            ]
        ),
    ];
}