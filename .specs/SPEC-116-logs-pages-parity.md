# SPEC-116 — Logs pages parity (`/dashboard/logs*`)

## Work

**Request logs** (`/dashboard/logs`):
- Toggle "Recording / Pipeline logs on" reflecting capture state.
- Search across model/provider/account/api-key; correlation-ID filter.
- Dropdowns: All models / All accounts / All API keys; provider filter as
  colored chips (multi-select); time presets 1h/1D/7D/30D.
- Table columns: STATUS (colored pill) / LOGGED / MODEL / REQUESTED /
  PROVIDER / REQUEST TYPE / API KEY / ENV; expandable row → request detail
  (reuse requestDetails payload viewer).
- "Clean history" + "Export" (keep CSV/JSONL).
- Server: extend `/api/usage` query params (search, providers[], from/to,
  type, apiKeyId, correlationId).

**Console logs** (`/dashboard/logs/console`):
- Dark terminal-style panel; colored level badges; level filter (All
  Levels→error/warn/info/debug); source filter; search; entry count +
  window selector; "See more logs" pagination.

**Proxy logs** (`/dashboard/logs/proxy`):
- Request-log table like upstream (status/type/level/provider pills,
  OK/ERR counters, recording toggle) fed by `proxy_logs` rows; keep pools
  cards as a secondary section.

**Timeline**: keep — verify matches upstream (already close).

## Tests (≥5)

- `/api/usage?search=` filters; `providers[]=a&providers[]=b` multi-select.
- Status pill colors by class (2xx green, 4xx amber, 5xx red).
- Console page filters by level.
- Proxy page renders proxy_logs rows.
- Export unchanged (regression).
