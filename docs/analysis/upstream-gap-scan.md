# Varredura upstream — features fora das specs (pós-SPEC-033)

Fontes: `diegosouzapw/OmniRoute@61e07fb` e `decolua/9router@ce4460e` vs `LLMRouter@44d5380` (main pós-PR #39).

Método: inventário de 739 rotas `src/app/api/**` + ~132 páginas do upstream, cruzado com o
conteúdo das 33 specs, os 121 endpoints mapeados em `LLMRouter.Server` e as páginas Razor.
A sidebar já espelha a nav upstream quase 1:1 — rotas sem página real caem no catch-all
`/dashboard/{*catchAll}` → `PlaceholderPage` ("planned in a later spec"). Os itens abaixo
são os que **nunca entraram no escopo de nenhuma spec** (SPEC-001..033), separados do que
as specs já cobriram parcialmente.

## A. Features inteiras ausentes (nenhuma spec tocou)

### Alto impacto

| # | Feature | Upstream | Estado LLMRouter |
|---|---------|----------|------------------|
| 1 | **Import de credenciais de CLIs locais** | `providers/{claude,codex,agy}-auth/{import,import-bulk,apply-local,export,zip-extract}`, `oauth/{cursor,kiro,trae,codex}/*` (login/poll/import/auto-import), `providers/zed/{discover,import,manual-import}`, `providers/command-code/auth/*`, `oauth/cliproxy-import` | Inexistente. SPEC-011 fez só OAuth genérico + cookie-paste. É o principal caminho de onboarding de contas reais no upstream. |
| 2 | **Lifecycle de sidecar services** | `services/{9router,bifrost,cliproxy,dario,mux,openwa,llmlingua}/{install,start,stop,update,auto-start,restart,status,provider-expose,auto-restart-adopted}`, `local/redis/*`, `services/[name]/logs`, `services/dario/admin/*`, `services/cliproxy/accounts` | Nossa `/dashboard/providers/services` é só catálogo de media kinds. Não há gestão de processos sidecar. |
| 3 | **Files API + batch files** | `/v1/files`, `/v1/files/{id}(/content)`, `/api/files*`, página `batch/files` | `/batches` existe sem files; página `batch/files` → placeholder. |
| 4 | **Jobs scheduler** | `jobs`, `jobs/{id}/{enable,disable,run-now,runs}`, `src/lib/jobs`, `jobRegistry` | SPEC-006 listou `jobs/` como fonte mas nenhum endpoint/página saiu. Proxy-pool health agendado (SPEC-027) ficou manual por causa disso. |
| 5 | **DB backup/export** | `db-backups/{export,exportAll,import}` | Não há export/import do SQLite pela UI (só `settings/export` de settings). |
| 6 | **Context engine pages + APIs** | 12 páginas `context/{aggressive,caveman,ccr,combos,headroom,lite,llmlingua,omniglyph,rtk,session-dedup,settings,ultra}`; APIs `context/{caveman/config,combos*,analytics*,rtk/{discover,import,learn,test,raw-output/[id]}}`, `compression/{compare,engines,language-packs,preview,retrieve,rules}` | SPEC-026/033 entregaram o pipeline + filtros RTK na página Token saver. Todos os links `context/*` da sidebar → placeholder. Engines individuais (LLMLingua, OmniGlyph, Caveman, CCR, session-dedup…) sem UI nem config por engine. |
| 7 | **Tunnels (expor o gateway)** | `tunnels/{cloudflared,ngrok,tailscale}/*` (install/enable/login/start-daemon) | Zero. |
| 8 | **Tokens VSCode (API por token)** | `v1/vscode/[token]/*` — endpoints estilo Ollama (`api/chat`, `api/show`, `api/tags`, `api/version`) + OpenAI (`chat/completions`, `models`, `responses`) + combos escopados por token | Zero. |
| 9 | **Evals** | `evals`, `evals/suites*`, página `analytics/evals` | Endpoint e página ausentes (link → placeholder). |

### Médio impacto

| # | Feature | Upstream | Estado LLMRouter |
|---|---------|----------|------------------|
| 10 | **Rate limiting** | `rate-limit`, `rate-limits` + enforcement | Não há rate limit por key/modelo. |
| 11 | **Cache de prompts** | `cache/{entries,reasoning,stats}` + páginas `cache`, `cache/media` | Inexistente (promptCache upstream). |
| 12 | **Keys avançadas** | `keys/groups*`, `keys/{id}/{access,devices,reveal,regenerate,usage-limits}` | `/keys` é CRUD básico. |
| 13 | **Quota avançado** | `quota/{groups,plans,pools/{usage,log,schedules},preview,keys/[id]/models}` | `/quota` simples + proxy-pools. |
| 14 | **Settings avançadas** | `settings/{ip-filter,free-proxies*,oneproxy(+rotate),payload-rules,reasoning-routing-rules(+simulate),task-routing,thinking-budget,tier-config,lkgp-cache,quota-store,system-prompt,auto-disable-accounts,background-degradation,cache-config(+embedding-options/test-embedding),models-dev,combo-defaults,purge-{call-logs,detailed-logs,logs,quota-snapshots,request-history,usage-history},authz-inventory,compression/{rules,run-telemetry,mcp-accessibility},cc-discovery-metrics,qdrant/*}` | `/settings` genérico + algumas chaves. Purge = só `/logs/prune`. Sem IP filter, payload rules, routing rules, free-proxy scraping, system-prompt injection, retenção configurável. |
| 15 | **Analytics/usage profundo** | páginas `analytics/{combo-health,compression,evals,search,utilization}`; ~25 endpoints `usage/*` (call-logs, combo-trace, route-explain, utilization, provider-window-costs, model-latency-stats, combo-scoring-inspector, combo-health-autopilot/dashboard, by-run, om-usage, provider-limits, key-quota, token-limits, cache-health, proxy-logs, requests-by-provider-date, budget/bulk, codex-reset-credit, glm-reset-card) | `/usage`, `/usage/daily`, `/usage/timeseries` só. Todas as subpáginas analytics → placeholder. |
| 16 | **Provider ops avançadas** | `providers/{health-autopilot(+actions),health-matrix,quota-windows,test-batch,bulk,deprecated,expiration,free-onboarding,openrouter-stats,web-session-contract,bulk-web-session}`, `providers/{id}/{interception-rules,param-filters,cc-alias,sync-models,refresh-cursor,chatgpt-web-codex-doctor}`, `volcengine-plan/connect/*` (device flow) | `/provider-connections` CRUD+test+nodes. |
| 17 | **Radar completo** | `radar/{catalog,intel(+sync),offers(+sync),referrals,settings,status,sync,sync-all}` + páginas `radar/{combos,intel,offers,setup}` | `/radar` é a página Extras genérica (scan local). Sem sync de catálogo/ofertas/referrals. |
| 18 | **Gamification completo** | `gamification/{badges(+earned),level,invite(+redeem),federation/{leaderboard,score},notifications,rotate,servers,stream,transfer,anomalies}` + página `gamification/admin` | `/gamification` + `/leaderboard` mínimos no Extras. |
| 19 | **Audit profundo** | páginas `audit/{a2a,mcp}`; APIs `mcp/audit(+stats)`, `compliance/audit-log` | `/audit` grava poucos eventos (pendências §10). |
| 20 | **Log export com destinos** | `log-export/{destinations(+run/test),status,types}` + página | `/log-export` é um GET único (fmt+limit); página → placeholder. |
| 21 | **Relay service** | `relay/tokens*`, página `relay`, `v1/relay/chat/completions(+bifrost)` | Não existe — relay é compartilhar o gateway com clientes externos via token. |
| 22 | **Session pools / sessions** | `session-pools`, `sessions` | Ausente. |
| 23 | **CLI device login** | `cli/{connect,tokens,whoami}`, `codex/connect/[token]`, página `connect/codex/[token]` | Ausente — CLI próprio não faz login via browser. |
| 24 | **OpenAPI explorer** | `openapi/{spec,try}`, página `docs/api-explorer` | `ApiEndpoints` é lista estática; sem spec gerada nem "try it". |
| 25 | **Playground extras** | `playground/{improve-prompt,presets(+id),simulate-route}` | Playground direto só. |
| 26 | **Skills extras** | `skills/{executions,collect/{chaos,detect,install},marketplace(+install),skillssh(+install)}` | Temos scan/toggle/install local (SPEC-009/026/031). |
| 27 | **Version manager + power** | `version-manager/{check-update,install,start,stop,status,restart}`, `restart`, `shutdown` | Ausente — self-update e restart via UI. |
| 28 | **A2A tasks** | `a2a/{tasks(+id,+cancel,+history),status}` | `/a2a` é um POST único (forward). Sem task lifecycle. |
| 29 | **Conductor completo** | `conductor/{ask,fleet,tasks(+id,+cancel)}` | Temos `conductor/{run,workflows}`. |
| 30 | **Qdrant / memory embeddings** | `settings/qdrant/*`, `memory/{embedding-providers,rerank-providers,engine-status,retrieve-preview,summarize,reindex,health}` | Memory = substring/FTS + backends kv/obsidian/notion. Sem embeddings vetoriais. |

### Baixo impacto / nicho

| # | Feature | Upstream | Nota |
|---|---------|----------|------|
| 31 | **Media gateway completo** | `v1/{audio/{speech,transcriptions,translations},images/{edits,upscale},videos/generations,music/generations,ocr,segment,moderations,multimodal-embeddings,rerank,search(+analytics),voices,text-to-speech/[voiceId],speech-to-text,web/{fetch,map},ws}` | SPEC-010 fez media-providers; gateway /v1 só cobre chat/messages/models/responses/embeddings. |
| 32 | **v1 management/agents** | `v1/{management/proxies*,management/proxy-subscriptions*,agents/{credentials,health,tasks*},accounts/[id]/limits,me/status,registered-keys*,session-leases,quotas/check,alpha/search,auto-combo/[channel]/candidates,classify,explain/routing,issues/report,provider-plugin-manifest,muse-code/models,video-bridge/drilldown,antigravity,combos}` | Gateway cobre só o core OpenAI/Claude/Gemini. |
| 33 | **Copilot / issue-agent / telegram / vnc** | `copilot/chat`, `issue-agent/runs`, `telegram/update`, `vnc-session/*`, `cursor-cli/[...path]`, `dahl/tokens` | Bots e agentes embutidos do upstream. |
| 34 | **Admin/misc APIs** | `admin/{concurrency,proxy-pool-visibility}`, `upstream-proxy/[providerId]`, `network/info`, `system/env/repair`, `storage/health`, `omniroute/{status,route/preview}`, `middleware/hooks`, `policies`, `tags`, `assess`, `intelligence/sync`, `headroom/{start,status,stop}`, `fallback/chains`, `search/{providers,stats}`, `monitoring/{compression,health}`, `health/{degradation,ping}`, `db/health`, `telemetry/summary`, `token-health` extra | Vários são endpoints de suporte às páginas placeholder. |
| 35 | **Páginas sem rota real** | `auto-combo`, `limits`, `log-export`, `radar/{combos,intel,offers,setup}`, `system/{1proxy,mitm-proxy}`, `tools/agent-bridge` (temos `/dashboard/agent-bridge`→Mcp), `api-manager/[id]/{access,routing}`, `combos/{playground,[id]}`, `media-providers/[kind]/[id]`, `miniapp`, `forgot-password`, `maintenance`, `privacy`, `terms` | Links da sidebar caem no PlaceholderPage. |

## B. Já decidido como fora de escopo

- **Electron desktop** — fora de escopo desde a spec original.
- **webdav-handler dev server** — nosso obsidian acessa o vault direto em arquivo (SPEC-033); upstream usa um WebDAV sidecar só pra dev.
- **9router** — subconjunto do OmniRoute (nenhuma página exclusiva).

## C. Sugestão de priorização

Se a meta é paridade funcional percebida, os maiores deltas visíveis ao usuário são:
1. **Context engine pages** (12 links da sidebar hoje = "coming soon") — (#6)
2. **Import de credenciais de CLI** — (#1) desbloqueia uso real sem criar conta do zero
3. **Analytics/usage profundo** — (#15) 5 páginas placeholder + telemetria rica
4. **Sidecar services + tunnels** — (#2, #7) fazem sentido juntos (expor/estender o gateway)
5. **Files API + jobs + db-backups** — (#3, #4, #5) completam o ciclo ops

O resto pode virar specs pequenas agrupadas por domínio (settings avançadas, keys/quota,
v1 extras, gamification/radar/audit).
