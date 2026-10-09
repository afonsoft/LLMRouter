# SPEC-039 — Rate limiting

Upstream: `rate-limit`, `rate-limits` + gateway enforcement. Today there is no
per-key / per-model throttling.

## Scope
- `rateLimits` table: {id, scope(apiKey|provider|model), scopeValue, rpm,
  tpm, burst, enabled}.
- `Core/Routing/RateLimiter.cs` — sliding-window counter in memory
  (per scopeValue, 1-min buckets + token counters), consulted in
  `GatewayPipeline` before dispatch; 429 with `Retry-After` + `rate_limit`
  error body.
- Endpoints: `GET/POST/PUT/DELETE /api/rate-limits`, `GET /api/rate-limit/status`
  (current window usage per scope for the caller's key).
- UI: `/dashboard/rate-limits` page — CRUD table + live status chips.
  Sidebar link.
- Tests: limit of 2 rpm → 3rd request 429; disabled rule ignored;
  per-model limit doesn't leak to other models.

## Non-goals
- Distributed rate limiting (single-node memory counters).
