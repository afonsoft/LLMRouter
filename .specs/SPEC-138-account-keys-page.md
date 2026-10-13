# SPEC-138 — User account: API Keys page (OpenRouter-style)

## Objetivo
Página self-serve `/account/keys` onde o usuário cadastrado gerencia as próprias
vk-keys — espelhando a tela "API Keys" do OpenRouter: nomear a key, definir
limite de gasto (credit limit), ver uso por chave e revogar. Junto com
`/account/billing` (SPEC-135) cobre "criar APIs, controlar gastos, limites e
pagamentos".

## Backend
- `PATCH /api/account/me/keys/{id}` passa a aceitar `budgetUsd` e
  `budgetPeriod` (none|daily|weekly|monthly) — hoje só name/modelsAllow/isActive.
  OpenRouter permite editar o limite depois de criar.

## UI (`Pages/AccountKeys.razor`)
- Tabela: Name | Key (masked) | Limit | Usage | Status | Created.
- Form "Create key": name (opcional), credit limit USD (0 = sem teto),
  period select, models allow (opcional, csv → JSON array).
- Key completa `vk-…` mostrada UMA vez em banner após criar.
- Por linha: editar limite inline, Disable/Enable (soft delete via isActive).
- Sidebar: item "API Keys" `/account/keys` no grupo account, junto de billing.

## Testes
- PATCH budget roundtrip via WebApplicationFactory.
- smoke.mjs: /account/keys renderiza tabela + create form.
