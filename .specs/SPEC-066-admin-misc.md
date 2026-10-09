# SPEC-066 — Admin & misc support APIs

Upstream support endpoints powering assorted pages: `admin/{concurrency,
proxy-pool-visibility}`, `upstream-proxy/[providerId]`, `network/info`,
`system/env/repair`, `storage/health`, `omniroute/{status,route/preview}`,
`middleware/hooks`, `policies`, `tags`, `assess`, `intelligence/sync`,
`headroom/{start,status,stop}`, `fallback/chains`, `search/{providers,stats}`,
`monitoring/{compression,health}`, `health/{degradation,ping}`,
`db/health`, `telemetry/summary`, `token-health`.

## Scope
- `GET /api/admin/concurrency` + PUT — global upstream concurrency cap
  (semaphore in GatewayPipeline).
- `proxy-pool-visibility` — which pools feed which providers (read+put).
- `GET /api/network/info` — egress IP, DNS, http version probe.
- `POST /api/system/env/repair` — fix file perms/paths under data dir.
- `GET /api/storage/health` + `/api/db/health` — disk, wal size, integrity.
- `GET /api/omniroute/status` — compat status blob; `route/preview` reuse
  SPEC-043 simulate-route.
- `middleware/hooks` + `policies` — pluggable request hooks registry
  (pre/post JSON rules, backed by SPEC-047 payload rules).
- `tags` — freeform tags on connections+keys with filtering.
- `assess` — runs provider capability probe (models/features discovered).
- `intelligence/sync` — refresh pricing/capability catalog from upstream feed.
- `fallback/chains` — view/edit fallback chain order per model.
- `search/providers` + `/stats` — search-provider registry + usage stats.
- `monitoring/{compression,health}`, `health/degradation`,
  `telemetry/summary`, `token-health` — aggregated status endpoints.
- `headroom` lifecycle — reuse SPEC-034 engine runner start/stop/status.
- UI: status cards into `/dashboard/status` + system page; hooks/policy
  editors in settings.
- Tests: concurrency cap queues; tags filter connections; db/health ok;
  fallback chain edit persists.
