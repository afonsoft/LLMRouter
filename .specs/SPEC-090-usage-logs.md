# SPEC-090 — Usage/logs depth (call-logs, request-logs, proxy-logs, log export)

Gap §3.2 usage group of `docs/analysis/paridade-2026-10-10.md`. The usage pages
exist; they need the upstream query endpoints behind them.

## Request-level log streams

Upstream exposes three filterable request-log views over `usageHistory` +
`requestDetails`:

- `GET /api/usage/call-logs` — per-LLM-call rows: id, at, provider, model, key,
  status, latencyMs, inputTokens, outputTokens, cost. Filters: `?provider`,
  `?model`, `?status`, `?key`, `?from`, `?to`, `?limit`, `?offset`, `?q`.
  Same shape as upstream (`{items, total}`).
- `GET /api/usage/call-logs/{id}` — single call row + its `requestDetails`
  payload (request/response/error).
- `GET /api/usage/request-logs` — per-HTTP-request view (one row per incoming
  request even when it fans out to several provider calls): path, method,
  status, duration, calls count. Group `usageHistory` by requestId where
  present.
- `GET /api/usage/proxy-logs` — egress proxy usage: which proxy served which
  call (proxyId column/log field), latency per proxy, failure rate.
- `GET /api/usage/by-run` — usage grouped by run/session id (`?runId=` filter,
  rollup rows: calls, tokens, cost per run).
- `GET /api/usage/om-usage` — OM (observability/meta) usage counters: totals
  per endpoint family (chat, embeddings, media, mgmt) for the settings-period.

## Log management extras

- `GET /api/logs/console` — ring-buffer of recent console/log lines
  (`Core/Logging` in-memory sink; `{items:[{ts,level,message}]}`,
  `?level`, `?limit`, `?tail`).
- `GET /api/logs/{id}` — log record detail incl. requestDetail payload.
- `GET /api/logs/export` — export filtered logs as `.jsonl` download
  (`?from`, `?to`, `?level`, `?format=jsonl|csv`); Content-Disposition file.

## Reset-credit cards (usage summary cards)

- `GET /api/usage/codex-reset-credit` — remaining-credit style card for codex
  providers (window %, resets-at, tokens-used vs cap from quota windows).
- `GET /api/usage/glm-reset-card` — same card shape for glm-family providers.

## Tests (≥7)

- call-logs returns filtered rows + total; filters provider/status/from work;
- call-logs/{id} includes requestDetails payload;
- request-logs groups multi-call requests into one row;
- proxy-logs attributes rows to proxies (seed via `x-proxy` field on usage);
- logs/export downloads jsonl with correct rows + filename header;
- console endpoint tails seeded log entries with level filter;
- by-run rollup aggregates calls under a runId.
