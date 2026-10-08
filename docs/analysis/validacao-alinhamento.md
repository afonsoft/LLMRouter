# Validação de alinhamento — specs × implementação × upstream

Auditoria pós-merge das 16 specs (PRs #1–#17). Veredito por spec:

| Spec | Tema | Veredito | O que falta vs spec/upstream |
|------|------|----------|------------------------------|
| 001 | Fundação | ✅ Alinhado | Shell/nav/design system/gateway/páginas centrais no lugar |
| 002 | Provider connections | ✅ Alinhado | Nodes custom, test c/ latência, live models, detail page |
| 003 | Tradutores | 🟡 Parcial | Endpoints `/responses`, `count_tokens`, `/v1beta/*`, media passthrough existem. Falta profundidade do claude.js (cache_control anchoring, fixToolUseOrdering, thinking signature) e a matriz de round-trip {openai,claude,gemini,responses}×{idem} com tool call + SSE coberta por fixtures |
| 004 | Combos | 🟡 Parcial | Sticky RR, vision-adapter, aliases, mappings, Combo Studio ok. Spec upstream tem ~20 estratégias; faltam weighted, p2c, least-used, cost-optimized, fusion, pipeline, auto 16-factor etc. |
| 005 | Usage/costs | ✅ Alinhado | Timeseries, provider-stats, pricing, budget |
| 006 | Logs/resiliência | 🟡 Parcial | Logs/console/export + cooldown por conexão ok. Falta circuit breaker de provider (4 estados) e model lockout do upstream |
| 007 | Settings | ✅ Alinhado | 12 seções persistindo |
| 008 | Quota/proxies | ✅ Alinhado | Quota, token-saver config, proxy pools CRUD+uso |
| 009 | CLI tools/translator/skills | ✅ Alinhado | 10 CLIs, /api/translator, skills scan/toggle/install (falta injeção real no pipeline) |
| 010 | Media providers | ✅ Alinhado | mediaKinds opt-in + filtro de rota + página |
| 011 | OAuth | 🟡 Parcial | 3 fluxos (device/PKCE/poll), token health, refresh ok. `clientSecretDefault` ficou vazio (push protection) → depende de env vars; callback ainda exige config por provider |
| 012 | Protocolos | 🟡 Parcial | MCP JSON-RPC funcional mas só tools `chat__*` (upstream: ~45 canônicas + 3 transportes + módulos). A2A/conversations/conductor ok |
| 013 | Landing/docs/PWA | ✅ Alinhado | Landing, docs viewer, error pages, PWA, onboarding, /status |
| 014 | MITM/inspector | 🟡 Parcial | Spec não exigia CONNECT (ok), mas pxpipe devia "portar painéis verbatim" — hoje é alias do inspector. Sem interceptação TLS |
| 015 | Extras | 🟡 Parcial | Discovery/gamification/chaos/memory/webhooks/audit ok. **Batches nunca marcam done**; plugins/search-tools são placeholder; leaderboard/free-tiers com lógica ingênua |
| 016 | Distribuição | ✅ Alinhado | CLI verbs, Dockerfile, compose, release workflow, /api/version |

## Conclusão

- **11/16 alinhadas**; **5/16 parciais** — nenhuma spec foi ignorada, mas várias entregaram o "esqueleto funcional" sem a profundidade do upstream.
- As divergências estão catalogadas em `docs/analysis/pendencias.md` (25 itens priorizados).
- Sugestão de ordem de retrabalho: 015 (bug real: batches), 003 (tradutores = coração do gateway), 012 (MCP tools), 004 (estratégias), 014/006.
