# OAuth CLI credential import

Import existing credentials from local CLI tools as provider connections.

| Endpoint | Source |
|---|---|
| `POST /api/oauth/{provider}/paste-credentials` | `omniroute-cred-v1.` blob from `omniroute login` helpers |
| `POST /api/oauth/codex/import` | Codex CLI `auth.json` accounts (bulk or single) |
| `POST /api/oauth/codex/import-token` | Single codex token or session JSON |
| `GET/POST /api/oauth/cliproxy-import` | CLIProxyAPI dir (`~/.cli-proxy-api`, override `CLIPROXYAPI_CONFIG_DIR`) |
| `POST /api/oauth/cursor/auto-import` | Cursor IDE / cursor-agent local creds |
| `POST /api/oauth/cursor/import` | Pasted cursor tokens |
| `POST /api/oauth/kiro/api-key` | Kiro / Amazon Q API key (`?targetProvider=`) |
| `POST /api/oauth/trae/import` | Trae refresh token |
| `POST /api/providers/agy-auth/import*` | Antigravity (Gemini CLI) creds; `apply-local` writes the CLI token file |

All endpoints require dashboard/management auth. Imported connections get the
same `Data` shape as OAuth flow tokens (`accessToken`, `refreshToken`,
`expiresAt`, `email`, `providerSpecificData`).
