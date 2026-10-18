# SPEC-130 — Provider auth apply-local/export + refresh

> Spec retroativo — implementado no PR #165, merge 2026-10-12. Fecha a fila
> SPEC-125..130 do `feature-coverage-audit-2026-10-11.md`.

Origem: último item da fila de feature-coverage — port das superfícies de auth
`/api/providers/{id}/*` do upstream que faltavam.

## Work

- **`codex-auth/apply-local`** — constrói `~/.codex/auth.json`
  (`{tokens:{access_token,refresh_token,account_id},last_refresh}`) a partir
  dos dados OAuth armazenados da conexão. Nunca sobrescreve um arquivo fresco
  existente salvo `{force:true}` — retorna
  `decision: "skipped_present_fresh"` vs `"written"` (verbatim upstream).
  Backup `.bak` sibling sempre feito na escrita.
- **`codex-auth/export`** — download attachment `codex-auth.json`.
- **`claude-auth/apply-local`** — merge de `claudeAiOauth`
  (accessToken/refreshToken/expiresAt/subscriptionType/rateLimitTier/scopes)
  em `~/.claude/.credentials.json`, preservando chaves não relacionadas
  (`mcpOAuthPreserved` reportado na resposta).
- **`claude-auth/export`** — attachment `claude-credentials.json`.
- **`{id}/refresh`** — passa por `OAuthService.RefreshAsync`
  (grant refresh_token); 404 id desconhecido / 502 `{error:"refresh failed"}`.

Todas as escritas gated por `LLMR_ALLOW_CONFIG_WRITES=false` →
**403 `{error, code:"writes_disabled"}`** (upstream
`ensureCliConfigWriteAllowed`).

## Tests

`Spec130ProviderAuthTests` (7): apply-local codex escreve auth.json correto +
backup, skip quando fresh (decision verbatim), force overwrite, claude merge
preserva chaves (`mcpOAuthPreserved`), export attachments, refresh 404/502,
writes_disabled → 403.
