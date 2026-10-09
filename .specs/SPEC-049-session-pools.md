# SPEC-049 — Session pools

Upstream: `session-pools`, `sessions`. Pools of warm upstream sessions
(e.g. ChatGPT-web session reuse) shared across requests.

## Scope
- `sessionPools`/`poolSessions` tables: pool {id,name,provider,strategy,
  maxSize}; session {id,poolId,connectionId,state,busyUntil,lastUsed,
  health}.
- `Core/Routing/SessionPool.cs` — acquire/release API: pick idle healthy
  session (round-robin or least-used), mark busy with lease TTL, release on
  response; unhealthy sessions evicted and refreshed via provider's session
  refresh hook.
- Gateway integration for providers marked `usesSessionPool` (web-session
  providers) — passes pooled session context instead of fresh login.
- Endpoints: CRUD `/api/session-pools`, `GET /api/session-pools/{id}/sessions`,
  `POST /api/session-pools/{id}/drain|refresh`.
- UI: `/dashboard/session-pools` page — pools + per-session state chips.
- Tests: acquire returns distinct sessions until pool exhausted; lease TTL
  frees busy session; drain resets states.
