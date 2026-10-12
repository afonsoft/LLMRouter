# SPEC-125 — OAuth local-CLI credential import

Source: feature-coverage-audit §proposed follow-ups (rank 1).

Port OmniRoute's credential-import surface so users import existing local CLI
credentials instead of re-doing OAuth per account.

## Endpoints (all management-auth)

- `POST /api/oauth/{provider}/paste-credentials` — `omniroute-cred-v1.<b64url>`
  blob `{v:1,provider,tokens:{access_token,...}}`; provider match required; optional `connectionId` upsert.
- `POST /api/oauth/codex/import` — `{accounts: obj|[]}` flat or `auth.json` (`tokens:{}`) shape; per-account results.
- `POST /api/oauth/codex/import-token` — `{accessToken,name?}` or `{session:"<auth.json>"}`.
- `GET|POST /api/oauth/cliproxy-import` — scan `CLIPROXYAPI_CONFIG_DIR||~/.cli-proxy-api/*.json`; GET previews (no tokens), POST imports.
- `POST /api/oauth/cursor/auto-import` — scan Cursor IDE `state.vscdb` + `~/.cursor/cli-auth.json`; returns creds (no persist).
- `POST /api/oauth/cursor/import` — persist pasted cursor creds.
- `POST /api/oauth/kiro/api-key?targetProvider=kiro|amazon-q` — `{apiKey,region?}`; long-lived key conn.
- `POST /api/oauth/trae/import` — `{refreshToken,userRegion?}`.
- `POST /api/providers/agy-auth/import` / `import-bulk` — antigravity creds.
- `POST /api/providers/agy-auth/apply-local?name=` — write accessToken to `~/.gemini/antigravity-cli/antigravity-oauth-token`.

Deviations: upstream validates codex refresh_token via live exchange before persist — we persist and let the runtime refresh path handle rotation. Upstream kiro validates via AWS call — we store the key (gateway executor consumes it).

## Tests
tests/LLMRouter.Tests/Spec125OAuthImportTests.cs — 10 tests.
