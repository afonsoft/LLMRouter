# SPEC-120 — Health + Leaderboard parity (`/dashboard/health`, `/dashboard/leaderboard`)

## Work

**Health**:
- Top banner: "LLMRouter is ready" + green "All systems operational"
  pill (degraded → amber/red state from existing health data).
- "Advanced diagnostics": Database health card (status, applied vs
  available migrations — read from `__migrations` vs files) with
  "Run Auto-Repair" action (admin-only, audits `db.autorepair`).
- Stat cards: Uptime, Version (from `/api/version`), Memory RSS,
  Providers (active/total).
- Keep existing Connection health table; add Session Activity and
  Quota Monitors sections fed by existing endpoints.

**Leaderboard**:
- Tabs: Overall / Weekly / Monthly / Tokens Shared.
- Stat cards (total requests, requests today, tokens today).
- Ranked table Rank/Name/Score — extend existing endpoint with window
  param; "Tokens Shared" tab ranks by shared-token volume.

## Tests (≥4)

- Health banner state reflects a seeded failing connection.
- Version + uptime cards render.
- Leaderboard window tabs hit the endpoint with correct param.
- Auto-Repair button requires admin (403 for non-admin).
