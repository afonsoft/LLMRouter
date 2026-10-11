# SPEC-117 — Analytics pages parity (`/dashboard/analytics*`)

## Work

**Utilization** (`/dashboard/analytics/utilization`):
- Stacked area/line chart of requests over the last 24h per provider
  (Chart.js already bundled? else lightweight SVG) + provider cards with
  utilization % and quota-bar + window label ("Oct 11, 05:00 PM").
- Server: `/api/analytics/utilization?window=24h` → per-provider time
  buckets + quota remaining (data exists in usageHistory/quota tables).

**Combo Health** (`/dashboard/analytics/combo-health`):
- "Combo Health Autopilot" card: summary counters (issues/degraded/
  healthy) + alert cards with HIGH/MED badge, text ("Combo success rate
  below target", "Target quota exhausted", "X has a terminal account
  state") and actions "Run cookbook" / "Open combo editor".
- Keep the existing per-combo score table below.

**Evals / Search**: verify field parity with upstream screens; fill minor
gaps (columns/filters) listed in the audit screenshots.

## Tests (≥4)

- Utilization endpoint returns buckets for seeded usageHistory.
- Combo-health summary counts match fixture rows.
- Alert card renders action buttons wired to existing endpoints.
- Chart renders with 0 providers (empty state).
