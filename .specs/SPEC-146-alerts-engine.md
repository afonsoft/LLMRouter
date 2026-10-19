# SPEC-146 — Alerts engine + UI (Antirion gap A)
Origem: docs/analysis/antirion-gap-2026-10-19.md §3.1.
- Tabelas: `alertRules` {id,name,kind:metric|event,expr JSON,severity,channelIds[],enabled},
  `alertChannels` {id,name,type:webhook|email|slack|push,config JSON},
  `alertInstances` {id,ruleId,state:firing|acked|snoozed|escalated|resolved,startedAt,resolvedAt,note},
  `alertDeliveries` {id,instanceId,channelId,status,attempts,lastError,at}.
- Avaliador no JobScheduler (60s): métricas de usageHistory (latência p50/p95, error rate,
  fallback rate, spend, cache hit rate) + eventos (guardrail hit, breaker open, budget>80%).
- Canais: webhook (POST JSON assinado HMAC), slack (incoming webhook), email via SMTP opcional
  (`settings.smtp`); deliveries com retry + `:redeliver` manual.
- API `/api/alerts*`: CRUD rules/channels, list instances, ack/snooze/escalate/resolve/note,
  deliveries + redeliver, `:test` por channel.
- UI `Pages/Alerts.razor` (inbox + rules + channels + deliveries, i18n 3 línguas) + sidebar link.
- Testes: evaluator firing/resolve, channel dispatch (mock handler), lifecycle transitions.
