# SPEC-011 — OAuth flows + provider detail auth modals

## Goal

OAuth connection creation end-to-end: device-code and PKCE/auth-code flows for the
22 `authType:"oauth"` providers, plus the auth modals from upstream.

## Upstream sources

- `src/lib/oauth/**` (providers/, services/, utils/, constants/) — flow engine
- `src/app/api/oauth/**`, `src/app/{authorize,callback}/**`
- `src/shared/components/{OAuthModal,OAuthModalPanels,CursorAuthModal,KiroAuthModal,KiroSocialOAuthModal,KiroOAuthWrapper,XiaomiMimoAuthModal,ZedAuthModal,GitLabAuthModal,IFlowCookieModal,ManualConfigModal}`
- Registry `oauth` blocks (clientId/secret, authorizeUrl, tokenUrl, deviceCodeUrl,
  redirectUri, fixedPort, callbackPath, scope, codeChallengeMethod, refresh*)
- `src/lib/tokenHealthCheck*` + `open-sse/services/tokenRefresh*` — refresh engine

## Scope

- `POST /api/oauth/{provider}/start` → device-code or PKCE initiation (returns
  user code + verification URL, or opens `/authorize` page); `GET /callback`
  completes and stores tokens in `providerConnections.data` (same JSON shape).
- `OAuthModal` Blazor port: QR/link display, code entry where needed, polling
  device token endpoint until granted.
- Token refresh service: background refresh `refreshLeadMs` before expiry
  (`refresh.encoding` form/json variants); circuit-breaker on repeated failures
  (`tokenRefreshCircuit`); `token-health` API + `TokenHealthBadge` in header.
- Cookie-session flows (`web-cookie` providers): IFlow/Kiro cookie-paste modals.
- `/dashboard/tokens` page (token list/expiry/refresh now) + `/dashboard/profile`.

## Tests

- Flow state machine with stubbed IdP endpoints; token refresh scheduling; expiry
  edge cases (past expiry, missing refresh token).
