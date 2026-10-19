# Análise — Antirion vs LLMRouter (2026-10-19)

Fonte: pitch do autor no TabNews ("Criei o AI gateway mais rápido do mundo") +
repo [`eliasbiondo/antirion`](https://github.com/eliasbiondo/antirion) (v0.1.1,
Apache-2.0, Rust — 28 crates em 8 camadas, console Next.js export estático
embutido no binário). Comparado com `main` @ `56aa1d5` (pós-SPEC-145).

> Escopo do pedido: **dashboards e funcionalidades** — o que podemos melhorar no
> nosso. Não é análise de paridade upstream (OmniRoute/9router) nem de mercado
> geral; é um gap-scan dirigido a um concorrente novo e bem feito.

## 0. TL;DR

O Antirion é um gateway **enterprise-first**: fraco em catálogo de providers
(~16 nativos vs nossos 288), sem combos, sem MITM, sem billing — mas **forte
onde nós somos fracos**: alerts engine, budgets com aprovação, guardrails
profundos, SSO/SCIM/MFA, change control, request log pesquisável, e uma console
de operação polida (saved views, facets, live tail, comparativo de período).

Sugestão de SPECs derivadas (seção 5): alerts, safety events + guardrails
extras, request-log saved views/facets, budget overage-approval, key scoping
(endpoints/IP/JWT), change-control leve, device-auth p/ CLI, SLOs, e um
test-mode API para e2e.

## 1. Arquitetura dos dois

| Eixo | Antirion | LLMRouter |
|---|---|---|
| Runtime | Rust, thread-per-core, `SO_REUSEPORT`, JSON splice parser (zero-copy do body) | .NET 10 / Kestrel, HotCache/HotReads, UsageWriter assíncrono |
| Deploy | 1 binário distroless (ou cluster PG+Valkey), data plane :8080, **admin plane :8081 separado** | 1 binário/imagem, porta única 20128 (admin e /v1 juntos) |
| Console | Next.js export embutido em `/ui`, estritamente API-driven (Admin API publicada como OpenAPI) | Blazor WASM, mesmo servidor, API `/api/*` interna |
| Store | embutido standalone; cluster = PostgreSQL + Valkey | SQLite embutido (`LLMROUTER_DB_PATH`) |
| Config | **um documento versionado** (gerações numeradas, diff, rollback, JSON Schema publicado) | `settings` JSON + kv + Config-as-code export/import (SPEC-104) |
| Perf publicada | p50 0,53 ms overhead @ 5k rps em 4 vCPU, 14k rps saturação, harness reproduzível em `bench/` | ~279 rps, p50 ≈ 9 ms (stub local) — medida interna, SPEC-105 |

## 2. Dashboards — tela a tela

Antirion tem 18 telas no console; comparamos com a contraparte nossa (~65
páginas reais). ✅ = já cobrimos; 🟡 = parcial; ⬜ = não temos.

| Tela Antirion | O que ela mostra | Nossa contraparte | Status |
|---|---|---|---|
| **Overview** | spend, reqs, latência, erro, cache-hit vs período anterior; leaderboards; requests recentes; get-started | Index + Activity hub (SPEC-142) | 🟡 falta comparação de período e leaderboards de spend |
| **Request log** | registros com rota/tentativas/tokens/custo/guardrails; **saved views**, **facets**, **live tail**, agregações; PII policy no conteúdo | Logs, LogsTimeline, LogsConsole, LogExport | 🟡 temos listas/export; faltam saved views, facets e live tail na UI |
| **Analytics** | projeção do mês, por Model/provider/team/deployment/guardrail; pressure panel; signals | Analytics (Index, ComboHealth, Utilization, Evals, Search), Costs | 🟡 cobrimos por model/provider; faltam dimensões team/key/person e projeção |
| **Alerts** | inbox de alertas, regras (métrica e evento), canais (Slack/PagerDuty/email/webhook/push), deliveries + redeliver, SLOs e disponibilidade diária | — (só notifications de gamification) | ⬜ **maior gap de dashboard** |
| **Budgets** | árvore org→team→project→key, ledger, counters, **overage requests approve/deny**, projeções, cost tools | Billing, Quota, RateLimits, CostsQuotaShare | 🟡 temos créditos/ledger/quotas; falta hierarquia + fluxo de aprovação de estouro |
| **Safety** | guardrails (14 tipos), eventos de segurança revisáveis, runtime, posture, coverage | ResponseGuard/PII (SPEC-107) + PromptGuard (SPEC-023), sem tela dedicada de eventos | 🟡 engine existe, UI de eventos não |
| **API keys** | anomalias, uso por key/hora, counters, **source addresses**, audit feed | AccountKeys, Tokens, ApiManager | 🟡 falta anomaly + source-IP tracking |
| **Teams & users** | users, teams, projects, orgs, roles customizadas por escopo, convites por e-mail, sessões, flags, SCIM | Users (SPEC-134), Signup | 🟡 temos users; faltam teams/projects/roles/convites/SCIM |
| **Models** | tiers, aliases, preços, deployments, saúde | Models + catálogo 288 | ✅ (nosso catálogo é mais amplo) |
| **Providers** | runtime readings, headroom de rate-limit, teste de conexão, saúde por chave | Providers, ProviderDetail, ProviderStats, Health | ✅/🟡 temos teste+health; falta "headroom" por key |
| **Cache** | status, **entries browser**, regras de invalidação + firings, savings, uso por hora/model/team/prefix | Cache.razor + semantic cache (SPEC-106) | 🟡 temos entries; faltam invalidation rules, false-positive report, savings analytics |
| **Routing** | árvore de rotas, tiers, saúde, share de tráfego por regra, live tail, defaults, simulate | Combos, ComboDetail, CombosLive, Conductor, simulate-route (SPEC-043) | ✅/🟡 temos simulate + strategies mais ricas; falta share-of-traffic por regra |
| **Change requests** | mudanças pendentes/aprovação dupla | — | ⬜ |
| **MCP** | servidores (remote/stdio/OpenAPI→tools), status, grants, tool approval, sessões | Mcp.razor + 45+ tools expostos | 🟡 expomos tools; não temos *gateway* de MCP servers externos |
| **Agents (A2A)** | cards, proxy atrás das mesmas chaves | A2a.razor + endpoints | ✅ |
| **Plugins** | hooks por estágio via webhook assinado ou WASM, JSON Patch, counters | PluginHooks (SPEC-031: setModel/addHeaders/annotate) | 🟡 temos hooks simples; faltam webhook/WASM e counters |
| **Settings** | config + versões, audit, nodes, auth, security, encryption, limits, observability, policies, governance, webhooks, audit sinks, SCIM, export jobs | Settings (~15 seções) | 🟡 cobertura ampla; faltam config-versions, audit sinks, nodes/cluster |

### 2.1 Detalhes de UX do console Antirion que valem copiar

- **Command palette** global com recents — temos Ctrl+K, mas o dele busca
  *entidades* (models/keys/routes) além de páginas.
- **Filtros na URL** em todas as listas (`useUrlFilters`) → link compartilhável
  reabre a mesma visão. Hoje nossos filtros de Logs/Usage não persistem na URL.
- **Starred/favorites + recents** por tipo de entidade.
- **`<CopyLinkButton>`** em todo detalhe de entidade.
- **CSV export** padronizado em todas as tabelas.
- **Time-range store** com janela de comparação (current vs previous period) na
  URL — base do "vs período anterior" de Overview/Analytics.
- **Graceful degradation**: console mais novo que o gateway mostra "isn't
  available on this gateway yet" por feature, em vez de quebrar.
- Atalhos por página (`f` filtros, `e` export, `n` new, `/` busca).
- CSP estrita: zero inline script/style no export — postura de segurança que
  podemos copiar no Blazor (hoje index.html tem JS inline).

## 3. Funcionalidades de gateway — gap por gap

### 3.1 Alerts engine (gap grande)
Regras sobre métricas (latência, erro, fallback, utilização, spend, projeção
de budget, eventos de guardrail, cache hit rate) e eventos; canais Slack,
PagerDuty, e-mail, webhooks e **browser push**; ciclo de vida
firing→ack/snooze/escalate/resolve; deliveries com redeliver; **SLOs com
disponibilidade diária**. Nós temos nada equivalente — só health page passiva
e notifications de gamification. **Prioridade alta.**

### 3.2 Budgets com governança
Budgets em $ ou tokens, diário→anual, calendário ou rolling, **soft / hard /
held-for-approval**, em 6 níveis (gateway/org/team/project/key/model) com
**overage requests** que um admin aprova/nega e grants temporários. Nosso
modelo: créditos/ledger (SPEC-111) + quotas por key + plan tiers (SPEC-144) —
cobre cobrança, não **governança de estouro**. Complementar, não duplicado.

### 3.3 Change control + config generations
Todo write sensível pode exigir aprovação de segunda pessoa (409
`change_request_required` → console oferece abrir change request); config é um
documento com gerações, diff e rollback. Temos ConfigAsCode import/export sem
versionamento nem dual-control. Baixo custo: tabela `configRevisions` +
diff-viewer + flag `requireApproval` em endpoints sensíveis.

### 3.4 Identidade empresarial
OIDC/SAML/GitHub SSO, SCIM, TOTP + WebAuthn, sessões do usuário, convites por
e-mail (SMTP), orgs/teams/projects/roles por escopo. Temos: senha local +
signup + users + roles simples; `TeamId` só reservado. **Roadmap multiuser
deve decidir até onde ir** — sugestão: teams+roles primeiro, SSO depois.

### 3.5 Virtual keys com escopo
Keys com allowed models/providers/**endpoints**/**IP ranges**/expiry, JWT keys
contra issuers, rotação, **anomalias** e source addresses. Temos AllowedModels
+ expiry + quota; faltam endpoint-scope, IP-scope, JWT-issuer e anomaly
detection (heurística simples: desvio de uso/hora + novo IP).

### 3.6 Routing avançado que não temos
- **Hedging** (disparo paralelo tardio e usa o primeiro que chega) — não temos;
- **Canary** (fatia de tráfego para deployment novo com error budget);
- **Shadow traffic** (espelha request p/ mirror e compara divergência);
- **Affinity** por sessão e por prompt-prefix (gruda no deployment que já tem o
  prefixo em KV-cache — grande ganho real de latência/custo);
- **Bandit/Thompson sampling** como estratégia de weighted;
- **Classificadores de conteúdo** e `cost_quality` (rota por classe do pedido);
- **Response predicates** (rejeitar 2xx inválido e tentar o próximo);
- Pool scheduling para self-hosted (vLLM) lendo carga/LoRA/prefix state.
Nossas 15+ strategies + auto-router cobrem bastante; os itens acima são
incrementais e plugáveis no `ComboStrategies`/auto-router.

### 3.7 Guardrails profundos
14 tipos: pii, secrets, keywords, regex, **json_schema**, **tool_permission**,
length, **moderation**, **llm_judge**, classifier, **topic**, template,
**webhook** (serviço externo) + parceiros; ações block/**redact
format-preserving**/flag/**divert**, em input, output **e streams**; eventos
revisáveis no console. Temos PromptGuard (injection) + ResponseGuard (PII).
Gaps: schema/tool-permission/llm_judge/topic, action `divert`, e a **tela de
safety events**.

### 3.8 Request log pesquisável
Facets + **saved views** + live tail + per-attempt timeline + cost ledger por
request + política de log de conteúdo (mask/hash/drop, zero-retention por
nível). Temos as tabelas (`requestDetails`) e páginas de logs — falta a camada
de busca/visões e a política de retenção de conteúdo.

### 3.9 Observability extra
Prometheus+OTLP (temos SPEC-101) + **HTTP log sinks** + **audit sinks**
(streaming do audit para endpoints HTTP) — audit nós já temos com hash-chain;
falta ship para fora.

### 3.10 Operação
- **Ingress pause/resume** e **drain** de nó — temos breaker/cooldown, não um
  "pause" global manual;
- readiness/liveness dedicados (temos `/api/monitoring/health`, serve parcial);
- **secrets backends** (Vault/AWS/GCP/Azure) — hoje env/DB apenas;
- cluster real é fora do nosso desenho SQLite — ignorar.

### 3.11 DevX/CI
- **Test-mode API** (`/test/clock`, `/test/seed`, `/test/reset`) — endpoint de
  admin que controla o relógio e semeia dados para e2e; cortaria muito hack no
  nosso `e2e/smoke.mjs`;
- **Device authorization flow** para sign-in de CLI (OAuth device code) — útil
  pro nosso `/api/cli/connect`;
- Benchmark harness público (`bench/` + `docs/benchmarks.md`) — temos
  `perf-results.txt` interno; publicar metodologia eleva credibilidade;
- **OpenAPI publicado** da Admin API — temos `/api/openapi/spec` gerado dos
  endpoints; falta versão estável/documentada;
- Supply-chain: SBOM + Sigstore + SLSA na release — nossa release publica
  binários e imagem; adicionar SBOM/provenance é barato no workflow.

## 4. Onde estamos à frente (não regredir)

- **288 providers** + catálogo com preços/capabilities vs ~16 do Antirion;
- **Combos + auto-router** (15+ strategies, fusion/pipeline/vision-adapter,
  virtual auto/*) — o roteamento dele é árvore estática mais simples;
- **Media providers** (tts/stt/image/video/music), MITM + traffic inspector,
  AgentBridge, CLI subscription pooling — ele não tem nada disso;
- **Billing/marketplace** (Stripe/MP/PagSeguro, créditos, free tiers,
  plan tiers) — ele tem budgets internos, não cobrança;
- **i18n** pt-BR/es + gamification + skills;
- Endpoints `/v1` extras: search, vscode pooling, relay, upstream-proxy.

## 5. Proposta de SPECs (prioridade × esforço)

| # | Spec sugerida | Prioridade | Esforço | Notas |
|---|---|---|---|---|
| A | **Alerts engine** — tabelas alertRules/channels/deliveries; avaliador no JobScheduler; canais webhook+email(SMTP opcional); UI Alerts (inbox/rules/deliveries); SLO diário | Alta | Grande | maior gap de dashboard; tela nova ~4 componentes |
| B | **Safety events + guardrails pack 1** — tabela safetyEvents; `json_schema`, `tool_permission`, `keywords/regex`, action `flag/divert`; UI Safety | Alta | Médio | engine já existe; é expansão + UI |
| C | **Request log pro** — saved views (kv), facets (by model/provider/key/status), live tail SSE, detail com tentativas+ledger | Alta | Médio | dados já existem em requestDetails |
| D | **Key scoping** — allowed endpoints + IP ranges + rotate; anomaly feed (desvio uso/hora + IP novo) | Média | Médio | estende apiKeys existente |
| E | **Budget overage approval** — `held` mode + overageRequests approve/deny + grant temporário | Média | Médio | encaixa em creditLedger/VirtualKeyGate |
| F | **Comparison windows + URL filters + saved/starred** — time-range store, `?from,to,cmp` na URL, favorites por entidade, copy-link, CSV padronizado | Média | Médio | UX transversal, ganho visível rápido |
| G | **Routing pack** — hedging + affinity por prompt-prefix + response predicates + shadow mirror | Média | Médio-Grande | hedging/affinity têm ROI real de latência |
| H | **Change control leve** — configRevisions (diff+rollback) + flag `requireApproval` p/ providers/keys/settings | Média | Médio | começa sem dual-person rule |
| I | **Device auth p/ CLI** — fluxo device-code consumindo `/api/cli/connect` | Baixa | Pequeno | UX de CLI |
| J | **Test-mode API** (`/api/test/seed|reset|clock`) — gated por env `LLMROUTER_TEST_MODE` | Baixa | Pequeno | acelera e2e/CI |
| K | **Audit/log sinks HTTP** — ship audit + logs p/ URL externa | Baixa | Pequeno | usa LogExporter existente |
| L | **Teams/projects/roles + convites por e-mail** | Baixa | Grande | decisão de produto (multiuser) — abrir discussão antes |
| M | **SSO OIDC (+SCIM depois)** | Baixa | Grande | idem — empresarial, talvez fora do foco docker |
| N | **Release hardening** — SBOM + provenance + scan na imagem | Baixa | Pequeno | edição no workflow release |

**Corte rápido recomendado:** A + B + C (os três são o que mais aparece no
dashboard dele e não temos), depois D/E/F. G é o único item de gateway que
muda métrica real (latência/custo via prefix-affinity). L/M são decisões de
produto, não de engenharia.

## 6. Referências no repo do Antirion

- `openapi/admin-api.yaml` — 249 endpoints, contrato completo (bom modelo p/
  nosso `/api/openapi/spec`);
- `console/README.md` — arquitetura do console (stores, padrões de UX);
- `docs/benchmarks.md` + `bench/` — metodologia de benchmark;
- `crates/antirion-router/src/{affinity,bandit,canary,classifier,shadow}.rs` —
  implementação das features de routing da seção 3.6;
- `crates/antirion-guardrails/src/` — os 14 tipos de guardrail.
