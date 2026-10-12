# SPEC-137 — Mercado Pago + PagSeguro (PIX/boleto/cartão BRL)

Mesma `IPaymentProvider` da SPEC-136, com `amountBrl` → `amountUsd` por
`settings.payments.brlPerUsd` (manual; default 5.60).

## MercadoPagoPayment
- Checkout: `POST https://api.mercadopago.com/checkout/preferences`
  (Bearer access token) — items[0]=creditos, `back_urls` success/pending/failure,
  `external_reference` = txnId. Retorna `init_point` (Checkout Pro cobre
  PIX, boleto e cartão).
- Webhook `POST /api/payments/mercadopago/webhook`: valida `x-signature`
  (HMAC sobre `id:{data.id};request-id:{x-request-id};ts:{ts};` c/ secret);
  `type=payment` → GET `/v1/payments/{id}` → `status=approved` liquida.
- `externalId` = payment id MP.

## PagSeguroPayment
- Checkout: `POST https://sandbox|api.pagseguro.com/orders` (Bearer token) —
  reference_id=txnId, customer, notification_urls, `checkout` link → `pay` URL.
- Webhook `POST /api/payments/pagseguro/webhook`: notification com `charges`
  → `status=PAID` liquida; segurança = re-fetch da order pela API (sem segredo
  compartilhado, mas fonte autoritativa).
- `externalId` = order/charge id.

## Comum
- Testes: mesma bateria do Stripe — checkout, webhook válido, replay, BRL→USD.
