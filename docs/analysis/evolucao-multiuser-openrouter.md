# Evolução futura — multi-usuário, venda de API estilo OpenRouter e multi-node

> **Status: direção aprovada para documentação (2026-10-11), não um plano de
> implementação.** Este doc registra o caminho arquitetural para transformar o
> LLMRouter de proxy pessoal em uma plataforma com usuários, chaves virtuais,
> rate limits por usuário e catálogo público de modelos — "o mesmo estilo do
> OpenRouter". Depende das specs base SPEC-101..110.

## 1. O que "estilo OpenRouter" significa aqui

OpenRouter é um gateway **multi-tenant de venda de acesso**: usuário cria conta,
recebe chaves próprias, recarrega crédito, escolhe modelo no catálogo público,
tem rate limit/quota/custo por usuário e logs de uso na própria conta. O roteador
(traduzir, escolher provider, fallback) é transparente.

Nosso equivalente: **o LLMRouter vira o gateway multi-tenant** — mantendo o modo
pessoal atual como "single-user mode". Três pilares:

1. **Multi-usuário/RBAC/OIDC** — contas, papéis, SSO, propriedade de recursos.
2. **Virtual keys + billing** — chaves por usuário/time com quota, budget, rate
   limit e ledger de crédito próprios.
3. **Superfície pública** — catálogo de modelos com preço, signup→chave
   self-service, docs, painel de uso para o usuário final.

## 2. Pré-requisito legal (decisão de produto — não pular)

Dois modos de "venda de API" têm perfis de risco muito diferentes:

| Modo | Base | Risco |
|---|---|---|
| **A. Revenda de chaves próprias** | Conexões com API keys pay-as-you-go reais (OpenAI, Anthropic, Groq, OpenRouter upstream…) | Baixo — é o modelo LiteLLM/one-api; margem = markup sobre custo |
| **B. Revenda de assinaturas OAuth** | Pool de Claude Pro/ChatGPT Plus/Gemini/Kiro etc. | **Alto — quase certamente viola os ToS dos providers** (conta pessoal licenciada ao titular). Uso pessoal (nosso caso hoje) já é zona cinzenta; cobrar terceiros por acesso ao pool amplifica o risco de ban e exposição legal |

Recomendação: o modo "venda de API" deve rodar sobre **modo A** (keys próprias
pagas pelo operador) — o pool de assinaturas continua feature de uso pessoal/
equipe interna, nunca produto vendável. Registrar aqui para a decisão ficar
explícita quando chegar a hora.

## 3. Arquitetura por fases

### Fase 0 — fundação (já especificado)

SPEC-101 OTEL/metrics · SPEC-102 routing hints por request · SPEC-103 retenção ·
SPEC-104 config-as-code · SPEC-108 usage sink async · SPEC-109 **migrations EF**
(obrigatória — as tabelas abaixo nascem de migrations reais) · SPEC-110 audit
hash-chain (multi-tenant torna integridade de auditoria requisito, não luxo).

### Fase 1 — multi-usuário + OIDC + RBAC

Novas tabelas (SPEC-109 migrations): `users(id, email, name, passwordHash?,
oidcSubject?, oidcIssuer?, role, isActive, createdAt)`, `userSessions`,
`auditEvents.actor` vira `user:<id>` (o campo já é livre).

- **Auth**: ativar o backend OIDC hoje 501 (`/api/auth/oidc/*`, páginas
  `authorize`/`callback` já existem) — providers Google/GitHub/Entra ID/Keycloak
  via `settings.oidc.providers[]`; manter password local como bootstrap +
  "admin de emergência".
- **RBAC**: papéis `admin | operator | member | viewer` — `admin` tudo;
  `operator` providers/combos/settings sem user-management; `member` usa o
  gateway e vê só o próprio uso/chaves; `viewer` read-only do próprio uso.
  Checagem por claim no middleware; `X-LLMR-Actor` no audit.
- **Propriedade**: `apiKeys.userId`, `providerConnections.ownerUserId`
  (null = global/admin). Usuário final nunca vê conexões upstream (só o catálogo
  exposto — como OpenRouter não mostra as contas upstream dele).

### Fase 2 — virtual keys, rate limit por usuário e ledger

- **Virtual keys** (modelo LiteLLM): `userKeys(id, userId, key, name,
  modelsAllow[], budgetUsd, budgetPeriod, rateLimitGroupId, isActive,
  spendUsd, createdAt)` — herda limites do usuário, restringe por cima.
  `apiKeys` atual ganha `userId`/`teamId` nullable → migra vira "chave de admin".
- **Rate limit por usuário** (estilo OpenRouter): extensão do `RateLimiter`
  com scopes novos `user:<id>` e `keyGroup:<id>`; regras compostas
  (req/min + tokens/min + concurrent). 429 retorna `retry_after` + headers
  `x-ratelimit-*` — igual ao contrato OpenRouter.
- **Ledger de crédito**: `creditLedger(id, userId, delta, reason
  {topup|usage|refund|adjust}, usageId?, createdAt, balanceAfter)` — custo por
  request sai do usageHistory com markup da tabela de preços (`pricing` admin
  já existe; adicionar `sellPrice` por modelo distinto do cost).
- **Billing (opcional, fase tardia)**: webhook Stripe/MercadoPago → `topup`
  no ledger; `redemptionCodes` estilo one-api como caminho sem PSP.
- **Dashboard do usuário**: rotas `/account/{usage,keys,billing}` — o usuário
  vê só seu próprio `usageHistory`/`requestDetails` (filtro obrigatório por
  userId em todos os endpoints de leitura — menor privilégio por padrão).

### Fase 3 — superfície pública OpenRouter-like

- `GET /v1/models` público com `pricing.sell` + `context_length` + `capabilities`
  (shape OpenRouter: `id, name, pricing{prompt,completion}, top_provider{}`).
- **Catálogo público** (`/models` na landing): tabela de modelos + preço de
  venda + status — SEO da oferta.
- Signup→key em 2 cliques: `POST /api/signup` (email+OIDC) → chave default
  emitida com quota inicial configurável (`settings.signup.initialCreditUsd`,
  trial mode).
- Request passa a aceitar `provider` hints por-chave (SPEC-102) **opcionalmente
  desligável por tenant** (`settings.hintsAllowed`) — seller pode não querer
  expor roteamento.
- Headers públicos de rota: `x-llmr-route` já cobre transparência; adicionar
  `x-llmr-cost` (preço cobrado) para o cliente auditar.
- Página `/status` pública por modelo (hoje existe a página; filtrar dados
  internos para fora).

### Fase 4 — Redis multi-node (só nesta ordem)

Só vale com multi-usuário real. Estado que hoje é in-memory e precisaria de
Redis/Valkey compartilhado entre réplicas:

| Estado hoje (single-node) | Vai para Redis quando multi-node |
|---|---|
| `RateLimiter` buckets sliding-window | counters/lua por scope (token bucket distribuído) |
| `PromptCache`/`semanticCache` | cache compartilhado (hit-rate sobe com réplicas) |
| `ProviderBreaker`/`CooldownTracker`/`ModelLockout` | estado de saúde upstream não pode divergir entre réplicas |
| `SessionPool`/`ConcurrencyGate` | leases distribuídos |
| `UsageSink` channel | fila compartilhada ou particionada por réplica + sink único |

Design: interfaces `IRateLimitStore`, `ICacheStore`, `IHealthStore` com
implementação `InMemory` (default, zero dependência nova) e `Redis` opt-in
(`settings.redis = {connectionString, prefix}`). **Não introduzir Redis como
dependência obrigatória** — o docker-compose de single-user continua igual.

## 4. Superfícies de config/parametrização novas

- `settings.tenancy = {mode: single|multi, signupEnabled, oidc{...},
  defaultRole, initialCreditUsd, hintsAllowed, publicCatalog}`.
- `settings.markup = {defaultPercent, perModel{}}` — preço de venda derivado
  do custo (ou tabela `sellPrice` explícita).
- Rate-limit groups nomeados (ex.: `free`, `pro`, `enterprise`) atribuíveis a
  usuário/chave — o equivalente aos tiers do OpenRouter.
- `apiKeys` migra conceitualmente: "chave do dono" → qualquer chave; toda chave
  pertence a alguém.

## 5. O que fica de fora / decisões abertas

- **Modo B (revenda de assinaturas OAuth)**: documentado §2 — não implementar
  como produto sem decisão explícita de risco.
- **Fraud/abuse**: signup aberto + trial credit exige anti-abuse (email verify,
  device fingerprint mínimo, caps) — estimar quando ativar `signupEnabled`.
- **KYC/compliance**: revenda de API pública pode cair em exigências fiscais
  (NF) e de conteúdo (log de prompts por exigência legal) — escopo fora do
  gateway, mas o `requestDetails` já retém payload caso exigido.
- **Escalabilidade real**: Postgres como backend alternativo ao SQLite quando
  multi-node (EF provider switch é trivial com migrations — outro motivo da
  SPEC-109 vir primeiro).
- **Orgs/times** (hierarquia org→team→key estilo LiteLLM enterprise): o modelo
  acima para em user→key; adicionar `teams` só se a venda B2B se confirmar.

## 6. Ordem sugerida de execução (quando aprovado)

1. SPEC-109 migrations → Fase 1 users/OIDC/RBAC (maior bloco)
2. SPEC-108 sink + SPEC-101 metrics (observabilidade antes de tráfego pago)
3. Fase 2 virtual keys + rate limit por usuário + ledger
4. Fase 3 catálogo público + signup
5. Fase 4 Redis/multi-node + Postgres — só quando houver >1 réplica
