# SPEC-046 — Settings ops: purge, retention, system prompt, tiers

Upstream settings keys: `purge-{call-logs,detailed-logs,logs,quota-snapshots,
request-history,usage-history}`, `system-prompt`, `tier-config`,
`thinking-budget`, `auto-disable-accounts`, `background-degradation`,
`combo-defaults`, `lkgp-cache`, `cc-discovery-metrics`, `quota-store`,
`models-dev`. Only `/logs/prune` exists.

## Scope
- Purge endpoints: `POST /api/settings/purge/{target}` for call-logs,
  detailed-logs, logs, quota-snapshots, request-history, usage-history —
  each with `?before=` cutoff; retention settings `retention.*` consumed by
  the SPEC-038 usage-prune job.
- `settings.systemPrompt` — text injected as first system message by
  GatewayPipeline when absent from the request (per-provider toggle map).
- `settings.tierConfig` — named tiers {priority, models[], limits} applied to
  apiKeys (ties into SPEC-041).
- `settings.thinkingBudget` — default/max thinking budget per model pattern,
  clamped in gateway.
- `settings.autoDisableAccounts` — after N consecutive errors auto-disable a
  providerConnection (fields on connection), re-enable job.
- `settings.comboDefaults`, `modelsDev` (pin local models-dev catalog),
  `quotaStore`, `lkgpCache`, `ccDiscoveryMetrics` — stored/gettable via
  /api/settings and consumed where named.
- UI: sections inside `/dashboard/settings` (purge buttons + retention
  inputs, system-prompt textarea, tier editor, thinking-budget).
- Tests: purge deletes only older rows; system-prompt injected exactly once;
  auto-disable after threshold.
