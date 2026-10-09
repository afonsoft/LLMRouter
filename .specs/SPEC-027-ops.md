# SPEC-027 — Ops: proxy pool health, health dashboard, kv index

Pendências #22, #24 + estrutural kv.

## Escopo
- Proxy pools: health check sob demanda (`POST /api/proxy-pools/{id}/check` testa todos proxies e grava status/latência) + rotação round-robin no uso.
- Health dashboard: `/api/monitoring/providers` agregando breaker state + cooldowns + lockouts + últimos erros por provider; seção no dashboard.
- kv: índice por item — `memory`/`webhooks`/`batches` migram para rows `kv` com key `items/{id}` ou tabela própria se simples (manter leitura compat).

## Aceite
- Testes: check grava status; monitoring retorna seções; leitura de memory/webhooks/batches continua funcionando após migração de formato.
