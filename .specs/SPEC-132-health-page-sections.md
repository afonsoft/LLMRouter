# SPEC-132 — `/dashboard/health` upstream page sections

Deferred from SPEC-131 (element fidelity). Ports the missing upstream
`/dashboard/health` sections into `Pages/Health.razor`.

## Upstream structure (page.tsx, 1191 lines)

1. `ConcurrencyQueuesCard` — `/api/admin/concurrency` → `{timestamp, comboQueues{}, semaphores{}}`
2. HealthSnapshot header — updatedAt, refresh, "Advanced diagnostics" toggle,
   Reset health (`DELETE /api/monitoring/health`), Unblock all
   (`POST /api/resilience/reset`)
3. Advanced section (toggle): `TelemetryCard` (`/api/telemetry/summary`),
   `ProviderHealthAutopilotCard` (`/api/providers/health-autopilot` GET/PUT +
   `/actions` sweep), `ProviderHealthMatrixCard` (`/api/providers/health-matrix`)
4. Database health card — `/api/db/health` + `/api/db/migrations` +
   `POST /api/db/autorepair`
5. Stat cards ×4 — uptime, version+runtime, memory RSS/heap, providers
   configured/active/monitored
6. Session Activity + Quota Monitors (2-col grid)
7. Graceful degradation card — checked/errors/errorRate chips
8. Provider health (circuit breakers) — legend healthy/recovering/down,
   per-provider rows (failures, retryAfter, last failure)
9. Rate limit status — configured limits table + "learned from headers"
   windows with remaining-% bars
10. Active lockouts
11. Connections table (kept from SPEC-120)

## Endpoint changes

- `GET /api/monitoring/health` — extended to upstream contract:
  `system{uptimeSeconds,version,runtimeVersion,platform,memoryUsage{rss,heapUsed}}`,
  `providerHealth` (breaker snapshot), `providerSummary{configured,active,monitored}`,
  `rateLimitStatus` (RateLimits table), `learnedLimits` (QuotaSnapshots grouped
  by provider), `lockouts` (ModelLockout), `quotaMonitor{active,alerting,
  exhausted,providers,monitors[]}` (QuotaWindows + ExceededAsync), `sessions`,
  `cooldowns` (CooldownTracker). Original `checked_/errors/errorRate/degraded`
  kept.
- `DELETE /api/monitoring/health` — resets ProviderBreaker + ModelLockout +
  CooldownTracker (upstream reset action).
- `GET /api/admin/concurrency` — extended to `{timestamp, comboQueues{},
  semaphores{admission{limit,running,queued}}}` (old `limit`/`inFlight` kept
  for compat).

## Verification

- Both endpoints verified against the real migrated OmniRoute DB
  (51 configured / 35 active providers reported).
- Client + server build clean; suite green.
