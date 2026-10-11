# Comparativo de mercado — LLMRouter vs gateways de LLM (2026-10-11)

Compara **LLMRouter** (pin `8b64b2c`, main 2026-10-11) com **OmniRoute**, **9router**
e outros gateways de LLM do mercado, nos eixos pedidos: **performance, auditoria,
configurações e parametrizações** — terminando no que podemos melhorar.

> Distinção importante: os docs anteriores (`paridade-*.md`, `routing-gap-*`) medem
> paridade **interna** com o upstream. Este mede posição **no mercado**.

## 1. Panorama — onde cada produto joga

| Segmento | Players | Foco | Posição do LLMRouter |
|---|---|---|---|
| **Proxy de assinaturas CLI** (nosso nicho) | OmniRoute, 9router, CLIProxyAPI, claude-code-router, anti-api, llm-interceptor, Quotio | Poolar contas OAuth de Claude Code/Codex/Gemini/Cursor/Copilot, traduzir formatos, espremer quota/token | **Player principal** — somos o rewrite .NET do par OmniRoute+9router |
| Gateway enterprise self-hosted | LiteLLM, Bifrost, Portkey, TensorZero, TrueFoundry, Gloo/agentgateway | Virtual keys, budgets, SSO, guardrails, observabilidade, HA | Funcionalidades adjacentes; sem multi-tenant |
| Gateway k8s/service-mesh | Envoy AI Gateway, Apache APISIX AI GW, Kong AI GW | CRDs/declarativo, extproc, token-based rate limit, escala de malha | Não competimos (somos single-binary/docker) |
| Router gerenciado (SaaS) | OpenRouter, Vercel AI Gateway, Unify, Eden | Billing unificado + provider routing por request | Diferente modelo; bom referencial de API |
| Observabilidade/edge | Helicone, Cloudflare AI GW, Langfuse | Logs, cache, evals, custo | Cobrimos parcial (usage/telemetry interno) |
| Channel manager (CN) | one-api, new-api | Revenda de tokens, quota, billing, grupos | Modelo comercial diferente |

## 2. Matriz comparativa

| | LLMRouter | OmniRoute (upstream) | 9router | CLIProxyAPI | LiteLLM | Bifrost | TensorZero | Portkey | Kong AI GW | Envoy AI GW | OpenRouter |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Stack | .NET 10 + Blazor + SQLite | Next.js/TS + SQLite | Next.js/TS + SQLite | Go | Python/FastAPI + Postgres/Redis | Go | Rust + ClickHouse | TS/node | nginx/Lua | Envoy/C++ + k8s | SaaS (fechado) |
| Providers | 288 (registry portado) | ~288 | ~40+ | ~8 CLI | 100+ | ~15 nativos + OpenAI-compat | ~18 + OpenAI-compat | 1600+ | qualquer HTTP | 16 | ~400 modelos |
| Overhead declarado | ~9 ms p50 (medido interno) | n/p | n/p | leve | ~40 ms/instância (contestado por Bifrost) | 11–59 µs | <1 ms p99 @10k rps | n/p | n/p | n/p | n/p |
| Throughput provado | ~279 rps single-node | n/p | n/p | n/p | 3000 rps (33 pods); ~1130 rps single | 424 rps @t3.medium; 5000 rps stress | 10k+ rps | n/p | escala nginx | 2000 rotas control-plane | escala Cloudflare |
| Auditoria | auditEvents + mcpToolCalls + compliance json/csv | usageHistory/requestDetails | básico | quase nulo | audit logs enterprise + per-team logging | logs + Maxim obs | inferências+feedback no ClickHouse | logs + SOC2/HIPAA | audit/SIEM enterprise | OTEL/envoy metrics | usage + API logs |
| Config | settings JSON + kv + UI 154 pág + 88 MCP tools | settings + UI ~70 pág + 69 MCP | settings + UI ~18 pág | config.yaml | config.yaml + Admin UI + DB | config + UI | config.toml + UI | configs versionadas + UI | declarativo/decK + CRDs | CRDs GitOps | dashboard SaaS |
| Parametrização de rota | combos (16 fatores, modePacks, qtSd), adaptiveRouting, glob mappings, keyAccess | idem + auto combos | combos auto-fallback | round-robin multi-conta | router strategies (least-busy, latency, cost, usage) + fallbacks | adaptive LB + fallbacks | variants A/B + retries/fallbacks + timeouts | conditional routing + retry/fallback/LB | plugins por rota | rate limit backend por token | `provider` object **por request** |
| Cache | exact-match SHA256 + HotCache | exact | n/d | — | Redis/S3/disk/**semântico** | **semântico** | — | simples + **semântico** | **semântico** (vectordb) | — | implícito upstream |
| Guardrails | PromptGuard regex (block/warn/log) | idem | — | — | guardrails + PII | guardrails | schemas | **50+ guardrails** + BYO | prompt/response/semantic guard | policies | zdr/data_collection flags |
| SSO/RBAC/multi-tenant | não (senha local + apiKeys) | não | não | não | sim (SSO/SCIM/RBAC — Enterprise) | virtual keys | auth própria | RBAC workspaces | enterprise | via k8s/authn | orgs SaaS |
| Assinaturas CLI/OAuth | ✅ Codex/Claude/Gemini/Kiro etc | ✅ (o original) | ✅ (origem da ideia) | ✅ core do produto | parcial | não | não | não | não | não | não |
| MITM/inspeção | ✅ forward-proxy + intercept por host + capture modes | ✅ AgentBridge 9 IDEs | ✅ básico | — | — | — | — | — | — | — | — |

n/p = não publicado.

## 3. Eixo — Performance

**LLMRouter hoje:** Kestrel/.NET 10, single binary, pipeline único com HotCache/HotReads,
SSE streaming, PromptCache exact-match (SHA256 do request canonizado), rate limiter
sliding-window in-memory, EF Core/SQLite com particionamento kv (SPEC-098). Medição
interna: **~279 rps sustentados, p50 ≈ 9 ms, p95 ≈ 458 ms** (`perf-results.txt`).

**Referências de mercado:**

- **Bifrost (Go)**: 59 µs overhead em t3.medium, 11 µs em t3.xlarge; 424 rps vs
  LiteLLM 44.84 rps em carga de 500 rps; stress a 5000 rps. *(getmaxim.ai/resources/benchmarks)*
- **TensorZero (Rust)**: <1 ms p99 overhead a 10k+ QPS; claim "25–100x+ menos latência
  que LiteLLM @100 QPS". *(github.com/tensorzero/tensorzero)*
- **LiteLLM (Python)**: benchmark próprio 3000 rps em 33 pods (latência gw p50 30 ms);
  em instância única ~40 ms de overhead — e degrada forte sob carga (o benchmark do
  Bifrost mediu p50 38.6 s a 500 rps). Python/FastAPI é o gargalo conhecido do mercado.
- **Kong/Envoy**: escala de proxy HTTP puro (C/Lua) — referência de teto, outro peso.

**Leitura:** para o nicho (proxy pessoal, dezenas de req/s no máximo) 279 rps é folga
de ~10–50x. Onde perdemos vs os líderes de perf: **cache semântico** (Portkey, Kong,
LiteLLM, Bifrost têm — é a maior fonte de "latência zero" deles), **estado
compartilhado** (rate limit e cache in-memory = single-node; não escala horizontal
sem Redis) e **escrita síncrona de usage** (usageHistory por request no SQLite — sob
rajada vira contenção; upstream usa better-sqlite3 síncrono igual, mas o mercado
enterprise usa batching/streaming para sinks).

## 4. Eixo — Auditoria

**LLMRouter hoje (SPEC-057):** `auditEvents` estruturados (actor/action/target/
meta/ip/at), `mcpToolCalls` (hash SHA256 dos args — sem payload, bom p/ segredos),
export compliance json/csv, `usageHistory` + `requestDetails` + `usageDaily`,
webhooks + deliveries, log-export destinations, MITM traffic inspector.

**Mercado:**

- **LiteLLM Enterprise**: audit logs de toda mudança de key/team/model com retenção
  configurável + per-team logging + SIEM via OTEL/Prometheus/Langfuse/Datadog/GCS.
- **Kong**: audit/SIEM enterprise + tracing nativo.
- **Portkey**: SOC2/HIPAA/GDPR, logs completos de request/response com redaction.
- **TensorZero**: cada inferência vira dado estruturado (episódio + feedback humano)
  — auditoria vira dataset de otimização.
- **Helicone**: auditoria é literalmente o produto (traces, sessions, user metrics).

**Gaps nossos:**
1. Sem export **OpenTelemetry/Prometheus** — só `/api/telemetry/summary` interno;
   todo o mercado enterprise exporta métricas/traces para Datadog/Grafana/OTLP.
2. Sem **política de retenção** por tabela (auditEvents/requestDetails/mcpToolCalls
   crescem para sempre; jobs scheduler já existe para implementar purge TTL).
3. Sem **assinatura do ator humano** — atores são `dashboard`/`apiKey:sk-…`/`system`;
   sem multi-usuário não há accountability individual (LiteLLM/Kong têm via SSO).
4. `requestDetails` guarda payload sem redaction de PII configurável (Portkey/Kong
   têm guard de response e sanitizers).
5. Sem integridade/tamper-evidence (hash-chain) — ninguém do nicho tem; seria
   diferencial barato de compliance.

## 5. Eixo — Configurações

**LLMRouter hoje:** settings blob JSON (kv particionado), `providers.json` estático
de 288 entradas, EF `EnsureCreated` (sem migrations), UI com 154 páginas, **88 MCP
tools** (maior superfície de config-por-agente do mercado), headless mode, Docker,
i18n en/pt-BR/es, db-backups export/import.

**Mercado:**

- **LiteLLM**: `config.yaml` + Postgres + Admin UI — config-as-code e UI convergem.
- **Kong**: declarativo (decK/konnect) ou DB — GitOps maduro.
- **Envoy AI GW**: 100% CRDs Kubernetes — config é manifest versionável.
- **Portkey**: configs versionadas com rollback.
- **CLIProxyAPI**: `config.yaml` único — minimalista como nós.

**Gaps nossos:**
1. Sem **config-as-code importável** — settings vivem só no SQLite; um `llmr-config.yaml`
   versionável (diff/import/validate) aproximaria do fluxo LiteLLM/Kong sem perder a UI.
2. Sem **migrations EF** — `EnsureCreated` dificulta upgrade de schema (upstream tem
   202 migrations; nosso particionamento lazy já sentiu isso na SPEC-098).
3. Secrets de conexão dentro do `data` JSON das connections — sem integração com
   secret stores (LiteLLM ent usa Vault/AWS SM; nós temos env vars como escape).
4. Sem **perfis/tenants** — settings são globais single-node (coerente com o foco
   pessoal; registrar como decisão).

## 6. Eixo — Parametrizações

**LLMRouter hoje** (mais rico do nicho, herdado do OmniRoute):
- Combos: fallback, round-robin+sticky, **intelligent scoring 16-fatores**
  (quota, health, costInv, latencyInv, taskFit, stability, tierPriority/affinity,
  specificityMatch, contextAffinity, cacheAffinity, sessionAvailability,
  resetWindowAffinity, connectionDensity, quality, reliability) + modePacks +
  budgetCap + explorationRate.
- **adaptiveRouting** allow/warn/deny + explanations; **ArenaEloSync** → fator quality.
- Model aliases + **glob mappings com priority**, `qtSd/` quota-key scoping,
  keyAccess allow-list, capability overrides, disabled models.
- Rate limits por apiKey/model/provider (req + tokens, sliding 60s).
- **param-filters por conexão** (drop params que o provider rejeita), ccAlias,
  interception rules, breakers/lockouts/cooldowns, quota windows/schedules,
  thinking-budget, task-routing rules, MITM intercept por host + capture modes.

**O que o mercado parametriza e nós não:**
1. **Routing por request** (OpenRouter `provider` object): `order`,
   `allow_fallbacks`, `require_parameters`, `zdr`, `data_collection`,
   `sort: price|throughput|latency`, `preferred_min_throughput` por percentil,
   `max_price`. *Nosso roteamento é 100% server-side (combo) — o cliente não pode
   pedir "mais barato" ou "só providers sem retenção" por chamada.*
2. **Cache semântico** com threshold configurável (Portkey/Kong/LiteLLM/Bifrost).
3. **Guardrails de resposta + semânticos** (Kong response guard via embeddings;
   Portkey 50+ checkers + BYO). Nosso PromptGuard é regex input-only.
4. **Experimentação/A-B** com feedback loop (TensorZero variants + evals → routing).
   Temos adaptiveRouting + ELO, mas sem experiment IDs/datasets.
5. **Rate limit distribuído por token** (Kong sliding-window Redis; Envoy token-based;
   LiteLLM Redis) — o nosso é in-memory/single-node.
6. **Virtual keys com herança** (LiteLLM: team→key→budget→models→tags). Nossas
   apiKeys são flat (tem AccessAllow/quota pools, mas sem hierarquia de billing).
7. **max_price / budget cap por request** — temos budgetCap de combo, não por chamada.

## 7. Veredito por eixo

| Eixo | vs nicho CLI-proxy | vs gateways enterprise | vs managed routers |
|---|---|---|---|
| Performance | **Líder** (único com perf publicável em .NET; 9router/OmniRoute não publicam) | Médio — bate LiteLLM single-node, abaixo de Go/Rust | n/a |
| Auditoria | **Líder** (auditEvents+MCP trail+compliance export; ninguém do nicho tem) | Gap: OTEL, retenção, SSO, redaction | — |
| Configurações | **Líder** (154 páginas + 88 MCP tools) | Gap: config-as-code, migrations, secret stores | — |
| Parametrizações | **Líder** (16 fatores + MITM + param-filters) | Gap: routing por request, cache semântico, response guard, A-B | Gap: provider object OpenRouter-style |

## 8. O que melhorar — priorizado

**Quick wins (alto valor / baixo custo):**
1. **OTEL + Prometheus**: `System.Diagnostics.Metrics` + OTLP exporter + `/metrics`
   (rps, latency por provider/model, tokens, custo, breaker state, cache hit-rate).
   Fecha o maior gap de observabilidade vs todo o enterprise. ~1 spec.
2. **Routing hints por request** (subconjunto OpenRouter): aceitar `provider.order`,
   `allow_fallbacks`, `require_parameters`, `max_price`, `sort` no body — o
   ComboPlanner já tem os dados (cost/latency/quota). Diferencial real no nicho.
3. **Retenção de auditoria**: TTL configurável + purge no jobs scheduler
   (auditEvents, mcpToolCalls, requestDetails, usageHistory rollup).
4. **Config-as-code**: `llmr-config export|import|diff` (JSON/YAML) cobrindo
   settings+combos+mappings+rateLimits — reuso do db-backups existente.
5. **Benchmark público**: publicar metodologia do perf gate (≥30 rps CI) +
   perf-results; hoje é o único dado de perf do nicho e está enterrado no repo.

**Médio prazo:**
6. **Cache semântico opt-in** atrás do PromptCache (embedding via provider local/
   OpenAI + threshold; cuidado: semântico é ruim p/ tráfego agentic — documentar).
7. **Response guardrails + PII redaction** (PromptGuard hoje é input-only; adicionar
   stage de response no pipeline + patterns de redaction configuráveis).
8. **Usage sink assíncrono** (channel + batch writer p/ usageHistory/requestDetails)
   — destrava throughput sob rajada e prepara multi-worker.
9. **Migrations EF** (EnsureCreated → migrations reais) antes de acumular mais
   schema ad-hoc.

**Estrutural (decisão de produto, não de engenharia):**
10. **Multi-usuário/RBAC/OIDC** — backend já é 501 com páginas prontas; só vale se
    o alvo sair de "proxy pessoal/docker" para "equipe pequena". Se não, documentar
    o posicionamento anti-enterprise como feature (simplicidade = feature).
11. **Estado distribuído (Redis opcional)** para rate limit/cache/breakers
    multi-node — só faz sentido com #10 (single-user nunca escala horizontal).
12. **Tamper-evident audit** (hash-chain nos auditEvents) — diferencial compliance
    barato, ninguém do nicho tem.

## 9. Referências

- LiteLLM: docs.litellm.ai/docs/{simple_proxy,benchmarks,enterprise,proxy/caching,proxy/virtual_keys}
- Bifrost: getmaxim.ai/resources/benchmarks · getmaxim.ai/blog/bifrost-a-drop-in-llm-proxy-40x-faster-than-litellm
- TensorZero: github.com/tensorzero/tensorzero · docs/gateway/index.mdx
- Portkey: github.com/Portkey-AI/gateway · portkey.ai/docs/product/ai-gateway
- Kong: developer.konghq.com/ai-gateway/policies · /plugins/ai-semantic-{prompt-guard,cache}
- Envoy AI GW: aigateway.envoyproxy.io
- OpenRouter: openrouter.ai/docs/guides/{routing/provider-selection,providers/traffic-routing}
- Helicone / Cloudflare AI GW / one-api / new-api: docs oficiais respectivos
- Nicho CLI-proxy: github.com/router-for-me/CLIProxyAPI · github.com/vitaly-z/9router ·
  github.com/diegosouzapw/OmniRoute (docs/FEATURES.md, wiki/Agentbridge)
