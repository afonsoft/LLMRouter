# SPEC-141 — User Activity page (`/account/activity`)
Escopo do usuário: ele vê SÓ os próprios dados (userId claim / vk key).
- Tabela generations dele (usageHistory filtrado por userId/userKeyId):
  timestamp, model, provider, tokens, cost, status, latency.
- Cards: spend/requests/tokens no range (7d/30d/all) + chart por modelo.
- Export CSV do range filtrado.
- API: `GET /api/account/me/activity?range=` + `GET /api/account/me/usage.csv`.
- Sidebar: item "Activity" junto de API Keys/Billing.
