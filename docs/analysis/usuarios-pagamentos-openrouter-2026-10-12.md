# Gestão de usuários, cadastro e pagamentos — LLMRouter vs OmniRoute vs OpenRouter (2026-10-12)

Comparação focada no bloco "conta e dinheiro": cadastro público, gestão de
usuários, chaves por usuário, créditos e integrações de pagamento
(Stripe, Mercado Pago, PagSeguro).

## OmniRoute (upstream)

**Zero cobertura neste bloco** — o OmniRoute é gateway single-user local:
uma senha de dashboard, nenhum modelo de usuário, nenhuma chave por usuário,
nenhum billing. Nada a portar; este bloco é evolução, não paridade.

## OpenRouter (referência de produto)

| Área | OpenRouter | LLMRouter hoje |
|---|---|---|
| Signup | público, e-mail/OAuth, conta + key na hora | `POST /api/signup` ✅ (enabled flag, rate-limit IP, cria User+vk-key+crédito inicial) — **sem página pública** |
| Gestão de usuários | admin implícito (org/team) | `GET/POST/PATCH/DELETE /users` + `/user-keys` + `/accounts/{id}/limits` ✅ — **sem UI** |
| Chaves | provisioning key → API keys nomeadas c/ limite de crédito | `UserKey` vk-* com `modelsAllow` + `budgetUsd/budgetPeriod` + rate-limit group ✅ mais completo que OR |
| Créditos | prepaid USD, ledger por uso | `creditLedger` append-only (topup/usage/refund/adjust, `balanceAfter`) ✅ — **sem UI de saldo/histórico** |
| Comprar créditos | Stripe (cartão) + Coinbase Commerce (crypto) | ❌ nada |
| PIX/BRL | ❌ (US-only) | oportunidade Brasil: Mercado Pago, PagSeguro |

## Gaps concretos → specs

| # | Spec | Escopo |
|---|---|---|
| 1 | SPEC-134 — users admin UI | `/dashboard/users`: tabela usuários (email/role/status/saldo), criar/editar/desativar, vk-keys por usuário, limites |
| 2 | SPEC-135 — signup + billing UI | `/signup` pública (layout do login), `/account/billing`: saldo, histórico do ledger, botão "Add credits" |
| 3 | SPEC-136 — Stripe | Checkout Session + webhook assinado → topup no ledger; `paymentTransactions` table |
| 4 | SPEC-137 — Mercado Pago + PagSeguro | mesma `IPaymentProvider`: MP preference (PIX/boleto/cartão via Checkout Pro), PagSeguro orders/redirect; webhooks idempotentes; BRL→USD |

## Arquitetura de pagamentos proposta

```
IPaymentProvider (Core.Payments)
  ├─ StripePayment      — Checkout Session + webhook checkout.session.completed
  ├─ MercadoPagoPayment — preference API (Checkout Pro) + webhook payment
  └─ PagSeguroPayment   — orders/checkout redirect + notification webhook

POST /api/payments/{provider}/checkout {amountUsd|amountBrl} → {checkoutUrl, txnId}
POST /api/payments/{provider}/webhook  (raw body + assinatura) → settle txn →
   creditLedger.Add(Delta=+usd, Reason="topup", ref=txnId)   [idempotente por txnId]

paymentTransactions: id, provider, userId, amount, currency, amountUsd,
   status (created|paid|expired|failed), externalId, checkoutUrl, createdAt, paidAt
```

- Config por provider em `settings.payments.<name>` (enabled, keys via
  `enc:v1`, currency, brlPerUsd).
- Webhooks validados por assinatura (Stripe `stripe-signature` HMAC; MP
  `x-signature`; PagSeguro notification re-fetch).
- Idempotência: `externalId` UNIQUE + settle só em `status != paid`.
- UI billing (SPEC-135) lê `GET /api/payments/providers` → mostra botões
  só dos providers enabled.
