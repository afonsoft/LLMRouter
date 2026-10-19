# SPEC-148 — Request log pro: saved views + facets + live tail (Antirion gap C)
Origem: docs/analysis/antirion-gap-2026-10-19.md §3.8.
- `logViews` kv/table {id,name,filters JSON} — saved views CRUD `/api/logs/views*`.
- `GET /api/logs/facets` — counts por model/provider/key/status/endpoint p/ sidebar de filtros.
- `GET /api/logs/tail` — SSE stream de requestDetails recém-inseridos (live tail, pausável).
- Detail já tem: rota/tentativas/tokens/custo — adicionar `attempts[]` timeline + `costLedger`
  (preço input/output/cached por parte) quando disponível.
- UI: Logs.razor ganha barra de facets + view picker + toggle live; filtros espelhados na URL.
- Política de conteúdo: `settings.logContent` {mode:full|mask|hash|drop} aplicado no detail
  (mask = PII patterns; hash = sha256:…; drop = não retorna body).
- Testes: views CRUD, facets counts, SSE tail recebe insert, logContent modes.
