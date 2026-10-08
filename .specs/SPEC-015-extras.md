# SPEC-015 — Extras: gamification, radar, discovery, free-tiers, misc pages

## Goal

Remaining OmniRoute pages (lower-priority features): gamification, radar, discovery,
free-tiers, leaderboard, batch, plugins, chaos, memory, webhooks, conductor extras.

## Upstream sources

- `src/app/(dashboard)/dashboard/{gamification/**,leaderboard,radar/**,discovery,free-tiers,free-provider-rankings,batch/**,plugins,chaos,memory,webhooks,search-tools,conversations,audit/**,a2a,quota?}/**`
- `src/lib/{gamification/,radar/,discovery/,freeProviderRankings*,free-tier?,batches/,plugins/,chaos/,memory/,webhooks/,localCorpus/,audit/}`
- `src/app/api/{gamification,radar,discovery,free-*,batches,plugins,chaos,memory,webhooks,search,audit}/**`

## Scope

- Gamification + leaderboard (usage-driven XP/badges per `gamification` lib + admin page).
- Radar: provider discovery/offer scanning (`radar` lib) + radar pages.
- Discovery page: local-network/discovered providers (Ollama detection — port
  `localHealthCheck`/`discovery` scanning: probe localhost:11434 etc.).
- Free tiers + free-provider-rankings (aggregate free provider health).
- Batch: `/v1/batches`-style jobs (batches lib) + batch pages + files page.
- Plugins page (plugin manifests), chaos page (fault injection toggles from
  `chaos` lib — percent error/latency injection into gateway), memory page
  (memory store CRUD + search), webhooks (URL + event subscriptions → dispatch on
  request/health events), search-tools, conversations viewer, audit log page
  (audit events already written by management ops).
- Remaining stub pages from SPEC-001 not covered by other specs.

## Tests

- Per-feature unit tests; chaos injection affects responses as configured.
