# SPEC-135 — `/signup` pública + `/account/billing`

## `/signup` (pública, fora do shell do dashboard)
- Mesmo split layout do login; campos nome+email+senha(opcional por enquanto
  — signup atual não recebe senha; adicionar `password` ao endpoint e gravar
  `passwordHash` pbkdf2 quando presente).
- POST /api/signup → mostra a vk- criada uma única vez + link "copiar" e
  "ir para login". Se `signup.enabled=false`, página mostra aviso.
- Toggle já existe em settings (`signup.enabled`, `initialCreditUsd`).

## `/account/billing` (dentro do dashboard, self-serve)
- Card saldo (último `balanceAfter`), tabela ledger paginada
  (data, delta, motivo, saldo) via `GET /api/credit-ledger` filtrado pelo
  próprio usuário.
- "Add credits": input valor + botões por provider habilitado
  (`GET /api/payments/providers`) → POST checkout → redirect `checkoutUrl`.
- Return pages: `/account/billing?payment=success|canceled` com toast.
- Minhas vk-keys: lista + criar/revogar (self-serve `/api/account/*` já existe).
- Testes: xUnit signup c/ password; e2e billing mostra saldo do admin.
