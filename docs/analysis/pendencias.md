# Pendências e gaps do port (LLMRouter vs OmniRoute/9router)

Gerado após o merge das 16 specs. Classificado por impacto.

## Alta prioridade (funcionalidade quebrada/incompleta)

1. **Batches nunca marcam `done`** — `POST /api/batches` enfileira e dispara Task.Run mas não persiste conclusão (comentário explícito no código). Status fica `queued` para sempre.
2. **OAuth sem `clientSecretDefault`** — removido do registry por push protection; providers que dependem de secret embutido só funcionam com env vars. Falta documentar/mapear `clientIdEnv`/`clientSecretEnv` por provider.
3. **i18n incompleto** — páginas novas (extras, inspector, tokens, mcp, conductor, onboarding, error) usam strings EN hardcoded; chaves `Loc.T` sem entrada nas locales caem no fallback. pt-BR/es parcial.
4. **MITM real não implementado** — só forward-proxy HTTP (absolute-URI) + CA para download. Sem CONNECT, sem interceptação TLS; página `pxpipe` é alias do inspector, não pipeline de mídia.
5. **Estratégias de combo** — implementado: priority/fallback, round-robin, sticky. Faltam ~17 do upstream (weighted, p2c, least-used, cost-optimized, reset-aware, headroom, quota-weighted, lkgp, context-optimized, cache-optimized, context-relay, fusion, pipeline, auto 16-factor).
6. **MCP limitado** — `tools/list` expõe apenas combos como `chat__*`. Upstream tem ~45 ferramentas canônicas (providers, keys, usage, settings, memory, skills, pools…) + transportes stdio/SSE. Faltam também os módulos memory/skill/GitHub/gamification/plugin.

## Média prioridade (paridade parcial)

7. **Resiliência** — CooldownTracker cobre cooldown por conexão. Falta circuit breaker de provider 4-estados (CLOSED/DEGRADED/OPEN/HALF_OPEN), model lockout por modelo e anti-thundering-herd.
8. **Guardrails** — sem prompt-injection guard nem validação de schema estilo Zod nas rotas do gateway; API key policy enforcement parcial.
9. **Responses API** — tradutores openai↔claude↔gemini ok; falta `/v1/responses` (Responses API ↔ Chat Completions transformer).
10. **Audit superficial** — só eventos de chaos/webhooks gravam; CRUDs de management não auditam. Falta endpoint de leitura completo + página (existe UI, poucos eventos).
11. **Memory** — kv simples com busca por substring; upstream usa memória com embeddings/sessões.
12. **Free-tiers** — detecção por substring `"free"` em `Data` é ingênua; upstream tem ranking por saúde/latência dos providers gratuitos.
13. **Conversations** — CRUD existe e o playground consome; falta página viewer dedicada (`/dashboard/conversations` lista mas não há tela de detalhe/busca).
14. **Skills** — scan de `skills/**/SKILL.md` + toggle/install ok; falta execução real de skills no pipeline (upstream injeta no system prompt/tooling).
15. **Docs viewer** — `MdToHtml` é parser mínimo (sem tabelas GFM, listas aninhadas, TOC).
16. **PWA** — cache-first básico; sem offline real para chamadas `/api`.

## Baixa prioridade (nice-to-have / fora de escopo anotado)

17. **Plugins/search-tools** — rotas existem, telas são placeholder.
18. **Local corpus, RTK, Notion/Obsidian MCP** — módulos upstream não portados.
19. **Electron desktop** — fora de escopo (anotado na spec).
20. **Context compression/token-saver** — config existe; falta pipeline real de compressão de contexto no chat.
21. **Chaos** — só errorPct+latency global; upstream injeta por rota/modelo.
22. **Proxy pools** — CRUD + uso no gateway ok; falta health check agendado/rotatividade automática.
23. **Cobertura de testes** — 137 testes unit/integration; zero E2E de UI (Playwright) no CI.
24. **Health dashboards** — uptime/status básicos; falta monitoramento por-provider em tempo real como o upstream.
25. **Porta padrão** — docs citam 20128 (Docker), dev usa 5159 (launchSettings) — alinhar/documentar.

## Bugs/estruturais conhecidos

- `kv` como storage genérico cresce sem índice por item (memory/webhooks/batches são JSON arrays num único row — risco de contention/limite sob uso pesado).
- `ForwardProxy` ignora `Proxy-Authorization` (sem autenticação no modo proxy).
- Algumas páginas novas ainda podem ter armadilhas Razor latentes (CS8978 em binds com nullables) — não cobertas por build-check de render.
