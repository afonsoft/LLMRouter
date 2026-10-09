# Pendências e gaps do port (LLMRouter vs OmniRoute/9router)

Gerado após o merge das 16 specs. Classificado por impacto.

## Alta prioridade (funcionalidade quebrada/incompleta)

1. **Batches nunca marcam `done`** — `POST /api/batches` enfileira e dispara Task.Run mas não persiste conclusão (comentário explícito no código). Status fica `queued` para sempre. ✅ SPEC-017
2. **OAuth sem `clientSecretDefault`** — removido do registry por push protection; providers que dependem de secret embutido só funcionam com env vars. Falta documentar/mapear `clientIdEnv`/`clientSecretEnv` por provider. ✅ SPEC-021
3. **i18n incompleto** — páginas usam `Loc.T`; en.json completo (190 chaves injetadas), pt-BR/es traduzidos nas chaves de maior tráfego; demais caem no fallback EN. ✅ SPEC-024/029 (parcial)
4. **MITM real não implementado** — só forward-proxy HTTP (absolute-URI) + CA para download. Sem CONNECT, sem interceptação TLS; página `pxpipe` é alias do inspector, não pipeline de mídia. ✅ SPEC-028
5. **Estratégias de combo** — implementado: priority/fallback, round-robin, sticky. Faltam ~17 do upstream (weighted, p2c, least-used, cost-optimized, reset-aware, headroom, quota-weighted, lkgp, context-optimized, cache-optimized, context-relay, fusion, pipeline, auto 16-factor). ✅ SPEC-020
6. **MCP limitado** — `tools/list` expõe apenas combos como `chat__*`. Upstream tem ~45 ferramentas canônicas (providers, keys, usage, settings, memory, skills, pools…) + transportes stdio/SSE. Faltam também os módulos memory/skill/GitHub/gamification/plugin. ✅ SPEC-025

## Média prioridade (paridade parcial)

7. **Resiliência** — CooldownTracker cobre cooldown por conexão. Falta circuit breaker de provider 4-estados (CLOSED/DEGRADED/OPEN/HALF_OPEN), model lockout por modelo e anti-thundering-herd. ✅ SPEC-018
8. **Guardrails** — sem prompt-injection guard nem validação de schema estilo Zod nas rotas do gateway; API key policy enforcement parcial. ✅ SPEC-023
9. **Responses API** — `/v1/responses` + `/responses/compact` roteados; tradutores cobrem output[]/output_text/usage e SSE `response.*`. ✅ SPEC-029
10. **Audit superficial** — só eventos de chaos/webhooks gravam; CRUDs de management não auditam. Falta endpoint de leitura completo + página (existe UI, poucos eventos). ✅ SPEC-021
11. **Memory** — kv simples com busca por substring; upstream usa memória com embeddings/sessões. ✅ SPEC-026
12. **Free-tiers** — ranking por saúde real (breaker + cooldown + latência/error-rate do usageHistory) com score. ✅ SPEC-029
13. **Conversations** — CRUD existe e o playground consome; falta página viewer dedicada (`/dashboard/conversations` lista mas não há tela de detalhe/busca). ✅ SPEC-024
14. **Skills** — scan de `skills/**/SKILL.md` + toggle/install ok; falta execução real de skills no pipeline (upstream injeta no system prompt/tooling). ✅ SPEC-026
15. **Docs viewer** — `MdToHtml` é parser mínimo (sem tabelas GFM, listas aninhadas, TOC). ✅ SPEC-024
16. **PWA** — cache-first básico; sem offline real para chamadas `/api`. ✅ SPEC-030

## Baixa prioridade (nice-to-have / fora de escopo anotado)

17. **Plugins/search-tools** — rotas existem, telas são placeholder. ✅ SPEC-025 (parcial)
18. **Local corpus, RTK, Notion/Obsidian MCP** — módulos upstream não portados. ✅ SPEC-025 (stub localCorpus; RTK/Notion/Obsidian não portados)
19. **Electron desktop** — fora de escopo (anotado na spec).
20. **Context compression/token-saver** — config existe; falta pipeline real de compressão de contexto no chat. ✅ SPEC-026
21. **Chaos** — só errorPct+latency global; upstream injeta por rota/modelo. ✅ SPEC-023
22. **Proxy pools** — CRUD + uso no gateway ok; falta health check agendado/rotatividade automática. ✅ SPEC-027
23. **Cobertura de testes** — 137 testes unit/integration; zero E2E de UI (Playwright) no CI. ✅ SPEC-030
24. **Health dashboards** — uptime/status básicos; falta monitoramento por-provider em tempo real como o upstream. ✅ SPEC-02x ✅ SPEC-027
25. **Porta padrão** — 20128 alinhado em dev (launchSettings), docker e README. ✅ SPEC-030

## Bugs/estruturais conhecidos

- `kv` como storage genérico cresce sem índice por item (memory/webhooks/batches são JSON arrays num único row — risco de contention/limite sob uso pesado).
- `ForwardProxy` ignora `Proxy-Authorization` (sem autenticação no modo proxy).
- Algumas páginas novas ainda podem ter armadilhas Razor latentes (CS8978 em binds com nullables) — não cobertas por build-check de render.
