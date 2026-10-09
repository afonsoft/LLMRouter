# SPEC-036 — usage/* endpoints + analytics pages

Fill the 5 analytics/* sidebar placeholders with real data pages backed by
usageHistory / requestDetails / compressionRuns.

## Endpoints (all /api, cookie-authed)
- GET /api/usage/history?limit&offset&provider&model&status — paged raw history
- GET /api/usage/analytics?days — summary: totals, successRate, top models/providers, daily series
- GET /api/usage/combo-health?days — per routing combo: requests, successRate, avgLatency, lastError
- GET /api/usage/utilization?days — per provider and per connection: requests, tokens, cost, errorRate
- GET /api/usage/model-latency?days — per model: count, avg, p50, p95
- GET /api/usage/requests-by-provider-date?days — pivot {date, provider, requests, tokens}
- GET /api/usage/key-quota — per apiKey: requests, tokens, cost (grouped by ApiKey)

## Pages
- /dashboard/analytics/combo-health — combo health table + per-combo score card
- /dashboard/analytics/utilization — provider + connection utilization
- /dashboard/analytics/compression — compression run telemetry (reuses SPEC-034 endpoints)
- /dashboard/analytics/search — request search over requestDetails/logs
- /dashboard/analytics/evals — combo scoring table (derived from usage)

## Tests
- seed usageHistory rows → each endpoint returns correct aggregates.
