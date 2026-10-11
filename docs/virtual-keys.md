# Virtual Keys, Rate Limit por Usuário e Ledger de Crédito (SPEC-111)

Modelo estilo OpenRouter/LiteLLM para venda de API: **usuários** donos de
**virtual keys** (`vk-…`), cada uma com allowlist de modelos, teto de gasto,
grupo de rate limit e atribuição de uso em **ledger de crédito**.

## Entidades

| Tabela | Papel |
|---|---|
| `users` | `id, email, name, passwordHash?, oidcSubject?, oidcIssuer?, role (admin\|operator\|member\|viewer), isActive` |
| `userKeys` | `id, userId, key (vk-…), name, modelsAllow (JSON globs "provider/model"), budgetUsd, budgetPeriod (none\|daily\|weekly\|monthly), rateLimitGroupId, isActive, spendUsd, spendPeriodStart` |
| `creditLedger` | append-only: `delta` (+crédito/−débito), `reason (topup\|usage\|refund\|adjust)`, `usageId?`, `balanceAfter` |
| `apiKeys` | ganhou `userId`/`teamId` nullable — linhas sem `userId` são chaves de admin/global |
| `usageHistory` | ganhou `userId`/`userKeyId` nullable — atribuição por request |

## Fluxo admin → usuário → chave → uso

1. **Admin cria usuário**: `POST /api/users {email, name, role}` (cookie dashboard).
2. **Admin emite virtual key**: `POST /api/user-keys {userId, name, modelsAllow, budgetUsd, budgetPeriod, rateLimitGroupId}` — a resposta traz `key` completo **uma única vez** (`GET` lista mascarado `vk-ab…yz`).
3. **Cliente chama o gateway**: `Authorization: Bearer vk-…` em `/v1/*`.
   - `modelsAllow` não-vazio e model fora da lista → `403 model_not_allowed`.
   - `budgetUsd > 0` e `spendUsd` do período ≥ teto → `402 budget_exceeded`.
   - Rate limit: regras `scope=user` (casa `userId`) e `scope=keyGroup` (casa `rateLimitGroupId`); 429 traz `Retry-After` + `x-ratelimit-limit-requests`, `x-ratelimit-remaining-requests`, `x-ratelimit-reset`.
4. **Uso é atribuído**: `usageHistory.userId/userKeyId`, `userKeys.spendUsd += cost` (reset lazy por período) e `creditLedger` com `delta=-cost, reason=usage, balanceAfter` — sempre pelo escritor serial (nunca calcule saldo em paralelo).
5. **Crédito**: `POST /api/credit-ledger/topup {userId, amount}`; histórico em `GET /api/credit-ledger?userId=`.

## Self-serve estilo OpenRouter (`/api/account/*`)

Autenticado **somente** por `vk-…` (chave admin/cookie → `403`):

- `GET /api/account/me` — usuário + key usada + saldo
- `GET /api/account/me/usage?days=` — histórico de uso do usuário
- `GET /api/account/me/ledger` — extrato de crédito
- `GET/POST/PATCH/DELETE /api/account/me/keys` — o usuário gerencia as próprias virtual keys

## Notas

- Autenticação: `apiKeys` primeiro (admin/global), depois `userKeys` — cookie de dashboard continua admin; um request com cookie + Bearer usa a **sessão do cookie**.
- `userKeys` é hot-cached (~15s); mutações nos endpoints invalidam o cache na hora — chave desativada para de autenticar imediatamente.
- Budget usa **reset lazy**: `spendUsd` zera quando `spendPeriodStart` sai do período corrente (não há job).
- `role` é informativo nesta fase — enforce de RBAC/OIDC vem na fase 1 completa do roadmap multi-usuário (`docs/analysis/evolucao-multiuser-openrouter.md`).

## Catálogo público e signup (SPEC-112)

- `GET /api/public/models` — catálogo OpenRouter-like (`{data:[{id,name,context_length,capabilities,pricing{prompt,completion,currency}}]}`). Público, sem auth. `settings.publicCatalog=false` → 404.
- Preço de venda: `settings.pricing["provider/model"].sell{input,output}` explícito vence; senão custo × (1 + `settings.markup.defaultPercent|perModel["p/m"]`%).
- `POST /api/signup` — opt-in via `settings.signup.enabled` (default **false** → 403). Rate-limit 5/h por IP → 429. E-mail duplicado → 409. Cria `User`+`UserKey`(vk-…)+topup `initialCreditUsd` no ledger; resposta 201 `{key,userId,creditUsd}` (a key só aparece uma vez).
