# SPEC-139 — Combo pricing (admin) com default dos modelos

## Objetivo
Admin define um preço para cada combo (resale). O campo vem com um default
sugerido calculado dos modelos selecionados (média das tarifas input/output
por 1M tokens do pricing catalog SPEC-005/118). Uma vez definido, o custo
cobrado do usuário no ledger passa a ser o preço DO COMBO (não o custo
upstream) — margem de revenda.

## Modelo
- `Combo.PricingJson` — `{"input":x,"output":y}` USD/1M tokens; null = cobrar
  o custo real (comportamento atual). Migration nova.
- `PricingService` já parseia esse shape (`ParsePrice`) — reusa.

## Backend
- `GET /api/combos` → cada combo ganha `pricing` (raw) e `suggestedPricing`
  (média in/out dos member models com preço conhecido).
- `PUT /api/combos/{id}` aceita `pricing` `{input,output}`; `null` limpa.
- `GatewayPipeline.LogUsageAsync(..., string? comboName = null)`: após o cost
  atual, se `comboName` casa um combo com `PricingJson` →
  `cost = (prompt*in + completion*out)/1e6` — preço do combo VENCE
  reportedCost e ComputeCost (preço de revenda, não custo).
- Call sites passam `comboName: target.ComboName` (upstream) / `model`
  (rotas provider="combo" — já é o nome do combo).
- Ledger/SendUsd já debitam row.Cost → fluxo automático.

## Admin UI (tela de combos existente)
- Coluna/editor "Price /1M tok (in · out)": input duplo prefilled com
  suggestedPricing; salva via PUT; "clear" volta a null (cobrar custo real).

## Testes
- Parse/roundtrip de pricing no PUT.
- suggestedPricing = média dos membros.
- LogUsageAsync com comboName+pricing → Cost = preço do combo.
