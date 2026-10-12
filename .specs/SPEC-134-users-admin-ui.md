# SPEC-134 — `/dashboard/users` admin UI (gestão de usuários)

Backend existe (SPEC-111/112); falta a tela. Estilo OpenRouter "users".

- Tabela: email, nome, role (admin|operator|member|viewer), status, saldo
  (último `creditLedger.balanceAfter`), criado em. Ações: editar role/limits,
  ativar/desativar, ver keys, ver ledger.
- Criar usuário: email, nome, senha opcional (pbkdf2), role, crédito inicial.
- Por usuário: lista `userKeys` (vk-, nome, budget, modelsAllow, ativo) +
  criar/revogar; limites (`/accounts/{id}/limits`).
- Endpoints já existem: `GET/POST /api/users`, `GET/PATCH/DELETE /api/users/{id}`,
  `GET/POST/PATCH/DELETE /api/user-keys*`, `GET /api/credit-ledger?userId=`.
- Seção nova na sidebar (grupo CONTA/ADMIN, após Rate limits).
- Testes: xUnit do CRUD já existente cobre backend; e2e smoke: página lista
  usuário admin e cria usuário de teste (cleanup depois).
