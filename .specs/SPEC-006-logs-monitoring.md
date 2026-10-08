# SPEC-006 — Logs, Monitoring & Resilience

## Goal

Observability parity: request logger with detail view, console log, activity timeline,
health page, resilience/cooldown tracking, log export.

## Upstream sources

- `src/app/(dashboard)/dashboard/{logs,logs/proxy,logs/console,logs/timeline,activity,health,runtime,resilience/**,log-export}/**`
- `src/shared/components/{RequestLogger,ProxyLogger,ProxyLogDetail,ConsoleLogViewer,JsonTreeExpandControls}`
- `src/lib/{proxyLogger*,logRotation*,logExport/,monitoring/,proxyHealth*,tokenHealthCheck*,jobs/,events/}`
- `src/app/api/{logs,monitoring,health,token-health,log-export,jobs,internal}/**`

## Scope

- `requestDetails` per gateway call (request/response bodies, headers redacted,
  latency, status, provider/model/connection) with size cap + retention cleanup job.
- `GET /api/logs` paged + filters (provider, model, status, date), `GET /api/logs/{id}`
  full detail with JSON tree view.
- `GET /api/console` — ring buffer of server log lines (mirror `consoleLogBuffer`):
  capture ILogger output, stream via SSE `GET /api/logs/stream` for live tail.
- `/dashboard/logs` (requests table), `/dashboard/logs/console` (live console),
  `/dashboard/logs/timeline` (per-request waterfall: resolve→connect→first-byte→done),
  `/dashboard/activity`.
- `/dashboard/health` — per-connection health status (last test, consecutive failures,
  cooldown-until), `/dashboard/runtime` (uptime, version, db size, GC stats),
  `/dashboard/resilience/connections` + `/resilience/cooldowns` (cooldown list +
  clear button).
- Circuit breaker: N consecutive failures → connection cooldown (upstream
  `tokenRefreshCircuit`/`proxyHealth` semantics); cooldowns surface in cascade order
  (skip cooled-down connections).
- `GET /api/log-export` (jsonl/csv download), retention settings under
  `/dashboard/settings/advanced`.

## Acceptance

- Every gateway request appears in logs with expandable JSON; failing provider enters
  cooldown and is skipped; console page live-tails.

## Tests

- Cooldown state machine; retention cleanup; log paging/filters; SSE stream emits lines.
