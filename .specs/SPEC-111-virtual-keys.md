# SPEC-111 — Virtual keys, rate-limit por usuário e ledger de crédito

Origin: `docs/analysis/evolucao-multiuser-openrouter.md` Fase 1 (fundação de
usuários) + Fase 2 — modelo LiteLLM/OpenRouter. Ordem aprovada pelo usuário:
após SPEC-109 (migrations) e SPEC-108/101.

## Work

- **Entidades** (EF migration `AddVirtualKeysLedger`):
  - `users(id, email, name, passwordHash?, oidcSubject?, oidcIssuer?, role,
    isActive, createdAt)` — `role`: `admin|operator|member|viewer` (informativo
    nesta fase; enforce vem com OIDC/RBAC na fase 1 completa).
  - `userKeys(id, userId, key, name?, modelsAllow, budgetUsd, budgetPeriod,
    rateLimitGroupId?, isActive, spendUsd, spendPeriodStart, createdAt)` —
    virtual keys `vk-…`; `modelsAllow` = JSON array de globs
    `"provider/model"` (`*` e `prefix*`); `budgetPeriod`: `none|daily|weekly|
    monthly` com reset lazy via `spendPeriodStart`.
  - `creditLedger(id, userId, delta, reason, usageId?, balanceAfter, createdAt)`
    — append-only; `reason`: `topup|usage|refund|adjust`.
  - `apiKeys` ganha `userId`/`teamId` nullable (migra vira "chave de admin").
  - `usageHistory` ganha `userId`/`userKeyId` nullable (atribuição de uso).

- **Auth**: `GatewayEndpoints.AuthenticatedKey` retorna `AuthKey` record
  `{Key, Authed, UserId, UserKey, ApiKeyRow}` com `Deconstruct` p/ compat —
  apiKeys primeiro (admin/global), depois `userKeys` (hot-cached via
  `HotReads.UserKeyAsync`). Cookie dashboard segue admin.

- **Gates no gateway** (Chat + Passthrough, antes do rate-limit):
  - `modelsAllow` não-vazio e model não casa → 403 `model_not_allowed`;
  - `budgetUsd > 0` e spend efetivo ≥ budget → 402 `budget_exceeded`
    (contrato OpenRouter `insufficient credits`).

- **Rate limit por usuário**: `RateLimiter.Matches` ganha scopes `user`
  (ScopeValue casa userId) e `keyGroup` (casa `userKeys.rateLimitGroupId`);
  assinaturas recebem `userId`/`keyGroupId` opcionais. 429 retorna
  `Retry-After` + headers `x-ratelimit-limit-requests`,
  `x-ratelimit-remaining-requests`, `x-ratelimit-reset` (contrato OpenRouter).

- **Ledger + spend**: `UsageWrite` carrega userId/userKeyId; `UsageWriter`
  (batch) e o caminho sync aplicam `UsageSideEffects`: `usageHistory.userId`,
  `userKeys.spendUsd += cost` (reset lazy de período), e `creditLedger`
  `delta=-cost, reason=usage, balanceAfter` quando userId != null.

- **Endpoints admin** (cookie, `/api`): `GET/POST /api/users`,
  `GET/PATCH/DELETE /api/users/{id}` (delete = isActive=false),
  `GET/POST /api/user-keys`, `PATCH/DELETE /api/user-keys/{id}` (POST retorna
  a chave completa uma única vez; list mascara `vk-ab…yz`),
  `GET /api/credit-ledger?userId=`, `POST /api/credit-ledger/topup`.
  Auditoria `AuditLog.RecordAsync` em toda mutação.

- **Endpoints self** (`/api/account/*` fora do RequireAuthorization, auth via
  `AuthenticatedKey` exigindo `UserId != null`): `GET me`,
  `GET me/usage?days`, `GET me/ledger`, `GET/POST/PATCH/DELETE me/keys`
  (self-serve estilo OpenRouter — chave admin (`userId null`) recebe 403).

- Doc `docs/virtual-keys.md` (pt-BR): fluxo admin→usuário→chave→uso→ledger.

## Tests (≥6)

- migração aplica as 3 tabelas + colunas novas em banco existente;
- virtual key autentica em /v1/*; chave inativa → 401;
- modelsAllow bloqueia fora da lista (403) e permite dentro;
- budgetUsd estourado → 402; reset lazy de período (unit);
- scope `user:` no RateLimiter → 429 com headers x-ratelimit-*;
- usage com userId gera linha no ledger com balanceAfter correto + spendUsd
  incrementado;
- `/api/account/me` com vk → 200 e dados próprios; com admin key → 403;
- CRUD admin de users/user-keys com máscara de chave.
