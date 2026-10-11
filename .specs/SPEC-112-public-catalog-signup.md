# SPEC-112 — Catálogo público de modelos + signup→key (estilo OpenRouter)

Origin: `docs/analysis/evolucao-multiuser-openrouter.md` Fase 3 (superfície
pública). Depende de SPEC-111 (users/userKeys/ledger).

## Work

- **`GET /api/public/models`** — endpoint público (sem auth) no shape do
  catálogo OpenRouter: `{data: [{id, name, context_length, capabilities[],
  pricing:{prompt, completion, currency:"USD"}, top_provider:{context_length}}]}`.
  `id` = `provider/model` das conexões ativas + combos; preços = tabela
  `settings.pricing` × markup (`settings.markup.defaultPercent` +
  `perModel{"provider/model"}` override; `sell` explícito na entrada de
  pricing vence markup). Ligado por `settings.publicCatalog` (default true).
- **`settings.markup`** documentado em `docs/virtual-keys.md`.
- **`POST /api/signup`** (fora do RequireAuthorization; gated por
  `settings.signup.enabled`, default **false**): `{email, name?}` cria user
  member ativo + emite uma `vk-` default + topup de
  `settings.signup.initialCreditUsd` (default 0) no ledger com
  `reason=topup`; retorna a chave uma única vez. Email duplicado → 409.
  Rate-limit primitivo por IP (5/hora em memória).
- Auditoria `user.signup` / `userkey.signup-create`.
- Doc: seção "Catálogo público + signup" em `docs/virtual-keys.md`.

## Tests (≥5)

- `/api/public/models` sem auth → 200, shape OpenRouter, preço com markup;
- `publicCatalog=false` → 404;
- signup disabled → 403; enabled cria user+vk+crédito e a vk autentica;
- email duplicado → 409;
- markup perModel override vence defaultPercent.
