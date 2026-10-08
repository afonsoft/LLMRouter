# SPEC-021 — Resilience depth + mitm pxpipe + oauth secrets

## Goal
Remaining parity items from SPEC-006/011/014.

## Scope
- Provider circuit breaker: CLOSED/DEGRADED/OPEN/HALF_OPEN per provider with
  lazy recovery, thresholds per auth profile (oauth/apikey/local), status in
  /api/monitoring; combo routing skips OPEN providers.
- Model lockout: provider+connection+model failure quarantine (429/404/model
  permission) without killing the connection.
- Anti-thundering-herd on concurrent failures.
- pxpipe page: real panels (media pipeline status per connection/kind) instead
  of alias.
- OAuth: read `clientIdEnv`/`clientSecretEnv`/`clientSecretDefaultEnv` from
  registry and resolve through IConfiguration/env vars; document required env
  names per provider on the tokens page.
- Audit coverage: write audit entries on providers/keys/combos/settings CRUD.

## Tests
- Breaker state machine transitions; lockout scoping; oauth env resolution.
