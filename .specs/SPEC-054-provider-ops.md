# SPEC-054 — Provider ops avançadas

Upstream: `providers/{health-autopilot(+actions),health-matrix,
quota-windows,test-batch,bulk,deprecated,expiration,free-onboarding,
openrouter-stats,web-session-contract,bulk-web-session}` +
`providers/{id}/{interception-rules,param-filters,cc-alias,sync-models,
refresh-cursor,chatgpt-web-codex-doctor}`.

## Scope
- `GET /api/providers/health-matrix` — connections × last-N-checks matrix.
- `health-autopilot`: settings + job (SPEC-038) that auto-disables bad
  connections and re-tests them; `POST .../actions` manual actions.
- `quota-windows`: per-connection window {kind(daily|weekly|monthly),
  limit,used,resetAt} on `providerConnection.Data` + enforcement in
  NodeResolver (skip exhausted connections).
- `POST /api/providers/test-batch` — parallel test of selected/all
  connections, returns per-connection {ok,latency,error}.
- `POST /api/providers/bulk` — bulk create/update/delete connections.
- `GET /api/providers/deprecated` + `expiration` — lists models/connections
  flagged deprecated or expiring.
- Per-connection: `interception-rules` (rewrite model/params), `param-filters`
  (drop params upstream rejects), `sync-models` (refresh model list from
  provider), `cc-alias` (model alias map). Stored in `Data` json.
- UI: extend provider-connections page — matrix tab, batch test button,
  per-connection rules editor.
- Tests: quota-window excludes exhausted connection; batch test returns
  per-connection results; param-filter strips a field.
