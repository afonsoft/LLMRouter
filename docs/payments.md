# Payments (SPEC-136/137)

Credit top-ups through hosted checkouts — Stripe (USD), Mercado Pago and PagSeguro (BRL).
A paid webhook appends a `topup` row to `creditLedger` and marks the `paymentTransactions`
row `paid` (idempotent — replays return `{"ok":true,"idempotent":true}`).

## Configuration

All config lives in the DB, `settings.data.payments` (edit via
`PUT /api/settings` or SQL):

```json
{
  "payments": {
    "brlPerUsd": 5.60,
    "stripe":     { "enabled": true, "secretKey": "sk_live_...", "webhookSecret": "whsec_..." },
    "mercadopago":{ "enabled": true, "accessToken": "APP_USR-..." },
    "pagseguro":  { "enabled": true, "token": "...", "baseUrl": "https://sandbox.api.pagseguro.com" }
  }
}
```

- `brlPerUsd` — BRL conversion for the Brazilian providers (default `5.60`). Checkout
  amounts are always entered in USD; the provider charges `amountUsd × brlPerUsd` in BRL.
- `pagseguro.baseUrl` — defaults to production `https://api.pagseguro.com`; point to the
  sandbox URL for testing.

## Endpoints

| Route | Auth | Purpose |
|---|---|---|
| `GET  /api/payments/providers` | user | enabled provider names (billing page selector) |
| `POST /api/payments/checkout` | user (`userId` cookie or `vk-` bearer) | `{provider, amountUsd}` → `{checkoutUrl}`; `provider:"auto"` = first enabled |
| `POST /api/payments/webhooks/{provider}` | anonymous, signature-verified | provider → settle |
| `GET  /api/payments/transactions` | user | last 50 own txs |

## Webhook verification per provider

- **stripe** — `Stripe-Signature: t=<ts>,v1=<sig>`; HMAC-SHA256 of `"{t}.{payload}"`,
  5-minute replay window, `FixedTimeEquals`. Configure the endpoint URL in the Stripe
  dashboard → `whsec_` goes into `webhookSecret`.
- **mercadopago** — notification `{type:"payment", data:{id}}` is verified by fetching
  `GET /v1/payments/{id}` with the access token (authoritative read); `external_reference`
  carries our tx id. Only `status:"approved"` settles.
- **pagseguro** — notification carries the checkout/order id; `GET /orders/{id}` with the
  token, `charges[0].status:"PAID"` settles. `reference_id` carries our tx id.

## Testing locally

```bash
# webhook replay without a provider account — sign a fake Stripe event
sqlite3 llmr.db "INSERT INTO paymentTransactions VALUES('tx1','<userId>','stripe','cs_test',25,'usd','pending',NULL,datetime('now'),NULL)"
# then POST a signed checkout.session.completed payload to /api/payments/webhooks/stripe
```

## Combo pricing (SPEC-139)

Na tela de combos o admin define **Price per 1M tokens (USD)** — `{"input","output"}`.
O campo mostra um valor sugerido = média das tarifas dos modelos do combo
(catalog overrides → synced → registry). `pricing=null` volta a cobrar o custo
real. Com preço definido, `LogUsageAsync` cobra do usuário o preço DO COMBO
(vence reportedCost e ComputeCost) e o ledger debita esse valor — margem de revenda.
