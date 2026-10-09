# SPEC-037 — DB backup / export / import

Upstream: `db-backups/{export,exportAll,import}`. Today only `settings/export`
(settings JSON) exists — no full SQLite export/restore from the UI.

## Scope
- `GET /api/db-backups/export` — streams the live SQLite file (safe `.backup`
  via SqliteConnection.BackupDatabase to a temp file) as download
  `llmrouter-YYYYMMDD-HHMMSS.db`.
- `GET /api/db-backups/export-all` — JSON export of every table
  `{tables:{name:[rows]}}` (portable, works cross-db-path).
- `POST /api/db-backups/import` — multipart or JSON body; replaces current DB:
  validates schema (PRAGMA user_version / table list), writes to temp file,
  atomically swaps in, re-opens DbContext. Requires admin.
- UI: card on `/dashboard/settings` (or Extras): Export .db / Export JSON /
  Import JSON with confirmation.
- Tests: export returns sqlite bytes; export-all round-trips row counts;
  import of a malformed file → 400 without touching the live db.

## Non-goals
- Scheduled backups (belongs to jobs spec).
