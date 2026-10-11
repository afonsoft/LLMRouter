# SPEC-103 — Audit/telemetry retention TTL + purge job

Origin: `comparativo-mercado` §8.3 — `auditEvents`, `mcpToolCalls`,
`requestDetails`, `usageHistory`, `webhookDeliveries` grow forever; LiteLLM
enterprise has per-surface retention. `JobScheduler` + jobs endpoints already
exist — add a scheduled purge.

## Work

- `settings.retention` = `{auditEventsDays, mcpToolCallsDays, requestDetailsDays,
  usageHistoryDays, usageDailyDays, webhookDeliveriesDays}` — defaults:
  auditEvents 90, mcpToolCalls 30, requestDetails 14, usageHistory 365,
  usageDaily 0 (never purge), webhookDeliveries 30. `0`/`null` = keep forever.
- `RetentionPurgeJob` registered in `JobScheduler` (daily + on boot after 5min):
  `DELETE FROM <table> WHERE At < cutoff` per surface — chunked deletes
  (e.g. 5k rows/batch) to avoid long SQLite locks; log counts per table.
- `GET /api/settings/retention` + `PUT` (merge into settings blob); expose last
  run + purged counts in `/api/jobs/{id}/runs` like other jobs.
- `usageHistory` purge must run **after** `usageDaily` rollup covers the window
  (rollup first, purge second — sequence inside the same job).
- Doc note: compliance export already exists — recommend exporting before TTL.

## Tests (≥4)

- seeded old/new rows per table → job deletes only expired;
- `0` disables purge for that table;
- chunked delete loop drains >1 batch;
- `GET/PUT retention` round-trips and clamps negative days to 0.
