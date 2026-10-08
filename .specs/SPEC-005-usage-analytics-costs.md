# SPEC-005 — Usage, Analytics & Costs

## Goal

Usage dashboard parity: summary cards, time-series charts, per-provider/per-model
breakdown, cost estimation, daily rollup — plus Analytics/Costs sidebar sections.

## Upstream sources

- `src/app/(dashboard)/dashboard/{usage,analytics,costs,provider-stats,activity}/**`
- `src/shared/components/{UsageStats,analytics/**}` + `UsageChart`, `ProviderBarChart`,
  `TopModelsChart` (recharts upstream → port to Chart.js via JS interop or
  Blazor-native SVG; prefer a thin JS wrapper over a vendored lib)
- `src/lib/{usage*,usageAnalytics*,spend/,pricingSync*,catalogUserPricing*}` ,
  `open-sse/services/usage/**`
- `src/app/api/{usage,analytics,provider-metrics,provider-stats,pricing}/**`

## Scope

- `usageHistory` rollups → `usageDaily` (dateKey → JSON: per-provider/model tokens+cost).
- Cost: registry `pricing` (per-model $/1M tokens in/out) + user price overrides
  (`catalogUserPricing`) → compute `cost` on each usage row.
- APIs: `GET /api/usage?range=`, `/api/usage/daily`, `/api/usage/by-provider`,
  `/api/usage/by-model`, `/api/usage/timeseries`, `/api/analytics/*`,
  `/api/provider-stats`, `/api/pricing` (+ sync from models.dev like `modelsDevSync`).
- Pages: `/dashboard/usage` (cards: today/week/month tokens+cost+requests; line chart;
  provider bar chart; top models), `/dashboard/analytics`, `/dashboard/costs` (+
  `/costs/pricing` table + `/costs/budget` monthly budget w/ alerts),
  `/dashboard/provider-stats` (latency p50/p95, error rate, requests),
  `/dashboard/activity` (recent request feed).

## Acceptance

- Usage page renders real data after a few gateway calls; charts match upstream layout;
  costs computed with registry pricing; daily rollup accurate at day boundary (UTC).

## Tests

- Aggregation correctness (tokens, cost math, daily rollup); API shapes.
