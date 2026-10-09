# SPEC-051 — VSCode tokens API

Upstream: `v1/vscode/[token]/*` — Ollama-style `api/chat`, `api/show`,
`api/tags`, `api/version` + OpenAI `chat/completions`, `models`, `responses`,
with combos scoped per token. Lets VSCode extensions (Continue, Cline via
Ollama provider) hit the gateway without dashboard auth.

## Scope
- `vscodeTokens` table: {id,token,name,defaultCombo,allowedCombos[],
  createdAt,active}.
- Route group `/v1/vscode/{token}/`: validates token, then serves:
  - Ollama surface: `POST api/chat` (translate to chat/completions and back),
    `GET api/tags` (combos+models as Ollama model list), `GET api/show/{m}`,
    `GET api/version`.
  - OpenAI surface: `POST chat/completions`, `GET models`, `POST responses`.
- CRUD `/api/vscode-tokens` for the dashboard.
- UI: card on `/dashboard/tools` (or keys page) — create token, copy URL.
- Tests: ollama api/tags returns combo list; chat round-trip via stub
  provider; bad token → 401.
