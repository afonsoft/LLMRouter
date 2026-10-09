# SPEC-064 — /v1 management & agents surface

Upstream `v1/*` extras: `management/proxies*`, `management/
proxy-subscriptions*`, `agents/{credentials,health,tasks*}`,
`accounts/[id]/limits`, `me/status`, `registered-keys*`,
`session-leases`, `quotas/check`, `alpha/search`,
`auto-combo/[channel]/candidates`, `classify`, `explain/routing`,
`issues/report`, `provider-plugin-manifest`, `muse-code/models`,
`video-bridge/drilldown`, `antigravity`, `combos`.

## Scope
- Token-authed (Bearer apiKey) read APIs for external agents:
  `GET /v1/me/status` (key identity, scopes, quota remaining),
  `GET /v1/accounts/{id}/limits`, `GET /v1/quotas/check?model=`,
  `GET /v1/combos`, `GET /v1/registered-keys` (own keys, masked).
- `POST /v1/classify` (route a prompt → suggested combo),
  `POST /v1/explain/routing` (why a request resolved the way it did —
  returns rule chain).
- `GET /v1/auto-combo/{channel}/candidates` — combo suggestions for a
  channel (cost|latency|quality strategies).
- `v1/management/*` — management-plane subset under admin auth:
  proxies CRUD mirror of /api, proxy-subscriptions.
- `POST /v1/issues/report` — client-reported error → audit event.
- `GET /v1/provider-plugin-manifest`, `/v1/muse-code/models`,
  `/v1/video-bridge/drilldown`, `/v1/antigravity` — compatibility stubs
  returning real data where possible (manifest, model list).
- `agents/*` + `session-leases` — agent task API reused from SPEC-053 a2a.
- Tests: me/status with real key; classify returns combo; explain/routing
  shows chain; quota check reflects usage.
