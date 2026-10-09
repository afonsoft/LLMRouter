# SPEC-042 — Log export destinations

Upstream: `log-export/{destinations(+run/test),status,types}` + page.
Today `/api/log-export` is a one-shot GET (fmt+limit).

## Scope
- `logExportDestinations` table: {id,name,type(webhook|file|s3-compatible),
  config(json),filters(provider/model/status/since),enabled}.
- `Core/Logging/LogExporter.cs` — formats jsonl/csv, POSTs webhook or writes
  file; last-run status persisted.
- Endpoints: CRUD `/api/log-export/destinations`,
  `POST /api/log-export/destinations/{id}/run` + `/test`,
  `GET /api/log-export/status`, `GET /api/log-export/types`.
  Optional `schedule` field wired to SPEC-038 jobs.
- UI: `/dashboard/log-export` page — destination list + run-now +
  last-run status (replaces placeholder).
- Tests: file destination writes jsonl; webhook posts to stub http endpoint;
  filters applied.
