# SPEC-048 — Relay service

Upstream: `relay/tokens*`, `relay` page, `v1/relay/chat/completions(+bifrost)`.
Lets the owner share gateway access with external clients via per-client
tokens, without giving them a dashboard account.

## Scope
- `relayTokens` table: {id,token,name,allowedModels[],quota,expiresAt,
  active,createdAt}.
- CRUD `/api/relay/tokens` (+ reveal once). Token auth on
  `POST /v1/relay/chat/completions` (Authorization: Bearer <relay token>) —
  routed through normal pipeline, usage attributed `ApiKey=relay:{id}`.
- Per-token quota enforced (requests/day + tokens/day via SPEC-039 limiter).
- UI: `/dashboard/relay` page — token list, create/revoke, usage per token,
  copy-connection-snippet.
- Tests: relay token hits /v1/relay/chat/completions (stub provider),
  revoked token → 401, quota exceeded → 429.
