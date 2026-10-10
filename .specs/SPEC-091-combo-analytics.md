# SPEC-091 — Combo/route analytics + budgets

Gap §3.2 analytics group of `docs/analysis/paridade-2026-10-10.md`. Combo health
scoring already exists (SPEC-076 `ProviderAvailability`, combo-health endpoint);
this adds the upstream inspection/forecast surface on top.

## Combo inspection

- `GET /api/usage/combo-health-dashboard` — per-combo dashboard rows: combo id,
  score, per-model availability states, error rates 1h/24h, last failure.
- `GET /api/usage/combo-forecast` — forecast next-window combo health: score
  trend + which models are projected to degrade (cooldowns, rising error rate,
  quota burn-down). Deterministic heuristic over usageHistory — no ML.
- `GET /api/usage/combo-scoring-inspector` — dump the 16-factor scoring input
  for a combo: `{comboId, models:[{model, factors:{...}, total}]}`.
- `GET /api/usage/combo-trace/{comboId}` — last N routings for the combo: which
  model was picked, why (score contributions), outcome. Uses requestDetails
  `x-routing-*` metadata where present.
- `GET /api/usage/route-explain/{requestId}` — post-hoc explanation of one
  routed request (the `/api/explain/routing` dry-run counterpart for a real
  logged request): candidates, eliminations, chosen, reason codes.

## Provider/model analytics

- `GET /api/usage/model-latency-stats` — p50/p95/p99 per model over `?window=`
  (from usageHistory.latencyMs).
- `GET /api/usage/provider-limits` — per-provider limit usage: rpm/tpm quotas
  (rateLimits rows), consumed this window, headroom.
- `GET /api/usage/provider-window-costs` — cost per provider per quota window
  (hours/days/weeks buckets).
- `GET /api/usage/token-limits` — per-key token caps vs usage (keyquota scope).
- `GET /api/usage/cache-health` — cache subsystem stats: HybridCache hits/
  misses (HotCache counters), kv blob sizes, hot scopes.

## Budgets

- `GET/PUT /api/usage/budget` — global budget config `{dailyUsd, monthlyUsd,
  warnAt}` persisted in settings blob.
- `POST /api/usage/budget/bulk` — set per-provider/per-combo budgets:
  `[{scope, id, dailyUsd}]` rows in kv `budgets`; `GET /api/usage/budget/bulk`
  lists them. `/v1` calls check budget before dispatch → 429
  `{error:{code:"budget_exceeded"}}` when the scope is over.

## Tests (≥8)

- combo-health-dashboard rows include availability + error rates;
- combo-forecast flags a model with rising error rate;
- scoring-inspector returns factor breakdown for a seeded combo;
- combo-trace lists routings with reason for a seeded requestDetails row;
- route-explain/{requestId} reproduces the logged decision;
- model-latency-stats percentiles over seeded latencies;
- provider-window-costs buckets costs by window;
- budget/bulk set→enforcement: over-budget combo → 429;
- cache-health exposes counters.
