# SPEC-128 — Provider passthrough fidelity: validation + retirement gate

> Spec retroativo — implementado no PR #163, merge 2026-10-12.

Origem: fila do `docs/analysis/feature-coverage-audit-2026-10-11.md`. A
superfície scoped `/v1/providers/{p}/*` (chat/completions, embeddings,
images/generations, voices, models, limits) já existia desde SPEC-088 — esta
spec fecha os deltas de fidelidade restantes vs as rotas upstream do
OmniRoute.

## Work

- **Retirement gate**: `p = cgpt-web` →
  `410 {"error":{"message":"Provider is retired and unavailable.","type":"provider_error","code":"PROVIDER_RETIRED"}}`
  (upstream `rejectRetiredCommonChatGptWebProvider`).
- **Validação de body por rota** em `RewriteBodyModel` (mensagens verbatim do
  upstream): JSON malformado → `400 "Invalid JSON body"`; não-objeto →
  `400 "Request body must be a JSON object"`; `model` não-string →
  `400 "model must be a string"` (antes delegado ou 500 em
  `GetValue<string>`).
- **Voices POST** exige `multipart/form-data` →
  `400 "Fish Audio voice creation requires multipart/form-data"` (rota voices
  upstream).
- **OPTIONS preflight** em `/v1/providers/{p}/{*rest}` retorna
  `Access-Control-Allow-Methods: GET, POST, OPTIONS` +
  `Access-Control-Allow-Headers: *` como toda rota scoped upstream.

## Tests

`Spec128ProviderPassthroughTests` (9) cobrindo o contrato inteiro: retirement
410, os três 400 de validação com mensagens exatas, voices multipart,
preflight OPTIONS.
