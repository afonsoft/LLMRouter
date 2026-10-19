# SPEC-156 — Audit + log HTTP sinks (Antirion gap K)
Origem: docs/analysis/antirion-gap-2026-10-19.md §3.9.
- `settings.sinks = [{id,name,type:audit|logs,url,headers{}}]` — config na Settings UI.
- Sink writer (BackgroundService + bounded channel): audit events (já gravados com
  hash-chain SPEC-110) e/ou requestDetails resumidos → POST NDJSON batch (5s/50 itens,
  retry 3x c/ backoff, drop-oldest se cheio).
- `POST /api/sinks/:id/test` (manda evento ping), `GET /api/sinks/:id/status` (fila, erros).
- Testes: batch enqueue, retry, status counters; sem sink → noop sem custo.
