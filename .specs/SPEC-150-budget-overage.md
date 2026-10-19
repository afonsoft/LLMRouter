# SPEC-150 — Budget overage approval workflow (Antirion gap E)
Origem: docs/analysis/antirion-gap-2026-10-19.md §3.2.
- `budgets` {id,scope:gateway|user|key|model,ownerId,limitUsd|limitTokens,period:daily|weekly|monthly,mode:soft|hard|held}
  — complementa (não substitui) creditLedger/quotas; enforce antes da chamada.
- `overageRequests` {id,budgetId,requester,amount,reason,state:pending|approved|denied,decider,decidedAt}
  — mode `held` cria request em vez de bloquear; approve → grant temporário
  `overageGrants` {id,budgetId,amount,expiresAt}.
- API `/api/budgets*`: CRUD, counters, requests approve/deny, grants revoke.
- UI Budgets.razor (nova ou extensão de Costs): counters + requests inbox + approve/deny.
- Testes: hard block, held→request→approve→grant, deny path, grant expiry.
