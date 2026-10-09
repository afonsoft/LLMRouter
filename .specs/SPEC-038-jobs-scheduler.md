# SPEC-038 — Jobs scheduler

Upstream: `jobs`, `jobs/{id}/{enable,disable,run-now,runs}`, `src/lib/jobs`,
`jobRegistry`. LLMRouter has zero scheduled execution — proxy-pool health
checks and quota-window resets are manual.

## Scope
- `Core/Jobs/JobScheduler.cs` — hosted service, tick every 30s; registry of
  `IJob { Id, Name, Interval, RunAsync }`; persisted state in new
  `jobRuns`/`jobState` tables (lastRun, nextRun, enabled, lastStatus, duration).
- Built-in jobs: `proxy-pool-health` (runs the proxy test already in
  SPEC-027), `usage-prune` (retention purge), `db-backup` (calls SPEC-037
  export-all to a backups dir).
- Endpoints: `GET /api/jobs`, `GET /api/jobs/{id}/runs`,
  `POST /api/jobs/{id}/enable|disable`, `POST /api/jobs/{id}/run-now`.
- UI: `/dashboard/jobs` page — table with next run, last status, run-now.
  Add sidebar link.
- Tests: register a stub job, force run-now, assert jobRuns row + status.

## Non-goals
- Cron-expression editing per job (fixed intervals + enable/disable first).
