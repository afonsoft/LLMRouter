# SPEC-050 — CLI device login

Upstream: `cli/{connect,tokens,whoami}`, `codex/connect/[token]`,
`connect/codex/[token]` page. Lets a CLI authenticate to LLMRouter by
opening a browser link (device-flow style).

## Scope
- `cliTokens` table: {id,token(12-char),state(pending|approved|revoked),
  createdAt,approvedAt,deviceName?}.
- `POST /api/cli/connect` — unauthenticated; creates pending token, returns
  `{token,url(/connect/<token>),expiresAt}`.
- `GET /connect/{token}` Razor page — shows token to logged-in dashboard
  user; Approve button → `POST /api/cli/tokens/{token}/approve` mints an
  API key bound to the token.
- `GET /api/cli/tokens/{token}` — CLI polls; pending → 202, approved →
  `{apiKey,baseUrl}`.
- `GET /api/cli/whoami` — with Bearer apiKey returns identity+permissions.
- Tests: full flow connect→approve→poll returns key; whoami validates;
  expired token → 410.
