# SPEC-136 — Stripe payments (topup → creditLedger)

- `Core.Payments.IPaymentProvider`: `CreateCheckout(userId, amountUsd, successUrl, cancelUrl)` e
  `ParseWebhook(rawBody, headers)` → `(externalId, amountUsd, status)`.
- `StripePayment`: `POST https://api.stripe.com/v1/checkout/sessions`
  (mode=payment, line item "LLMRouter credits", success/cancel URL) — auth Bearer
  `payments.stripe.secretKey`. Webhook `POST /api/payments/stripe/webhook`:
  verifica `Stripe-Signature` (HMAC-SHA256 `t.v1` sobre `t.{body}`, tol 5min) com
  `webhookSecret`; só `checkout.session.completed` liquida.
- `paymentTransactions` table + migration: id, provider, userId, amount,
  currency, amountUsd, status, externalId UNIQUE, checkoutUrl, createdAt, paidAt.
- Settle: txn `paid` → `creditLedger` delta+`amountUsd` reason `topup`
  (grava `paymentTxnId`); re-post de webhook = no-op.
- `GET /api/payments/providers` → providers enabled p/ UI.
- Settings: `payments.stripe.{enabled,secretKey,webhookSecret,currency}` (enc:v1).
- Testes: checkout cria txn+URL (HttpMessageHandler mock); webhook assinatura
  válida liquida 1x (replay = no-op), assinatura inválida → 400.
