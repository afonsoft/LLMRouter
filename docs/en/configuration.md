# Configuration

## Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `ASPNETCORE_URLS` / `LLMROUTER_PORT` | `http://+:20128` | HTTP bind |
| `LLMROUTER_DB_PATH` | `~/.local/share/LLMRouter/llmrouter.db` | SQLite path (`/data/llmrouter.db` in docker) |
| `LLMROUTER_ADMIN_PASSWORD` | — | Seed admin password on first boot |
| `LLMR_SYNC_WRITES` | `0` | `1` = telemetry writes inline (tests) |
| `LLMR_NADIR_API_KEY` / `OMNIROUTE_NADIR_API_KEY` | — | Nadir decision API key (auto-router) |
| `LLMR_NADIR_BASE_URL` / `OMNIROUTE_NADIR_BASE_URL` | `https://api.getnadir.com` | Nadir base URL |
| `LLM_PROVIDER` / `LLM_BASE_URL` / `LLM_API_KEY` / `LLM_MODEL` | — | External LLM used by tests/agents |
| `{PROVIDER}_OAUTH_CLIENT_ID/SECRET` | — | OAuth device flows (tokens page) |

## Settings (`settings` row `data` JSON)

| Key | Shape | Effect |
|---|---|---|
| `autoRouter` | `{explorationRate, sla:{targetP95Ms,maxErrorRate,maxCostPer1MTokens,hardConstraints}, lkgp:{enabled}, nadir:{apiKey,baseUrl,timeoutMs}}` | auto-router tuning |
| `comboAutoPruneStaleSteps` | `true/false` | auto-prune stale combo steps on sync |
| `comboDefaults` | `{kind, stickyLimit, ...}` | defaults applied to new combos |
| `memory.backend` | `kv`/`obsidian`/`notion` | memory store backend (+ `obsidian.vaultPath`, `notion.token`, `notion.parentId`) |
| `rateLimits` | rules array | sliding-window limits per key/provider |
| `retention.usageDays` | `30` | usageHistory prune (job) |
| `freeTier.providers` | array | free provider pool |
| `guardrails.*` | toggles | prompt injection checks |
| `rtk.*` | filter config | request-body token saving filters |
| `compression.*` | mode/overrides/pipeline | context compression |
| `ipFilter`, `payloadRules`, `reasoningRoutingRules`, `taskRouting`, `oneproxy` | objects | settings-routing features |

## Providers & connections

- `providers.json` — 288 providers (id, format, models, capabilities, auth
  scheme `bearer|cookie|none`, baseUrl) — registry is a singleton.
- `providerConnections` — a credential/instance of a provider: `Data` JSON
  holds `apiKey`, optional `baseUrl` override, `customHeaders` (per-connection
  extra upstream headers — hop-by-hop + auth names are filtered),
  `quotaDaily`/`quotaMonthly` caps, expiration metadata.
- `syncedModels` — live-fetched model catalogs per provider (sync via
  `POST /api/synced-available-models`); `modelCapabilityOverrides` patches
  capability flags per model.

## API keys

`apiKeys` rows: `key`, `isActive`, limits (rpm/tpm/dailyTokens), rules,
optional plan (`quotaPlans` + `quotaSchedules`), groups (`keyGroups`).
Gateway auth accepts `Authorization: Bearer`, `x-api-key` or `?key=`.

## Jobs (settings `jobs.*`)

Built-in: `proxy-pool-health` (15 m), `usage-prune` (24 h),
`db-backup` (24 h → `backups/`, keeps 14), `log-export` (5 m, per-destination
schedule). Managed at `/api/jobs*` or `/dashboard/jobs`.
