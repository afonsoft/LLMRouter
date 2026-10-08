# SPEC-017 — Batches fix + extras completion

## Goal
Finish the loose ends of SPEC-015: batches that never complete, plugin/search-tools
placeholders, smarter free-tiers/leaderboard.

## Scope
- Batch lifecycle: persist `status` transitions queued→running→done/failed with
  per-item results (kv `batches` per-job rows, not one JSON array); `GET /api/batches/{id}`
  returns progress + results; run items in a hosted background worker (or scoped
  Task.Run that updates the DB via IServiceScopeFactory).
- Plugins page: list "plugin manifests" — for parity, show bundled skills + mcpServers
  as installed plugins (real data, not placeholder).
- Search-tools page: real content — index of gateway endpoints + docs links.
- Free-tiers: derive from registry (`kinds`/`free` flags where present) + connection
  health (testStatus/cooldown state) instead of naive substring.
- Leaderboard: also rank providers (not only models).

## Tests
- Batch job reaches `done` with results after worker runs.
- Free-tier detection ignores non-free `Data`.
