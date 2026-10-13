# OpenRouter Logs / Activity / Pricing vs LLMRouter — 2026-10-13

Fontes: openrouter.ai/docs/guides/features/{logs,activity}, /pricing.
Comparado contra as telas admin (`/dashboard/*`) e do usuário (`/account/*`).

## O que o OpenRouter tem que falta aqui

### Logs (doc: features/logs)
- Tabs por tipo: Generations | Upstream Requests | Sessions | Batches
- **Generation detail sheet**: overview (model, provider, cost, tokens, finish
  reason, streaming, latência upstream+throughput, região, fallbacks),
  **Provider Responses waterfall** (cada tentativa com latência), cost
  breakdown (cache discount, BYOK, extras), Prompt & Completion completos,
  Raw JSON, "replay in playground".
- Filtros persistidos na URL (view compartilhável), histograma de requests,
  filter por generation ID direto.
- **Input & Output Logging**: opt-in em Settings>Observability — default só
  metadata (privacidade). Sem flag, prompt/completion não são guardados.
- API: `GET /api/v1/generation?id=` (meta) + `/generation/content` (conteúdo).

Nós temos: `/dashboard/logs` filtros+meta expansível, `proxyLogs` journal,
`requestDetails`. **Faltam**: detail sheet com waterfall de tentativas,
prompt/completion opt-in + raw JSON, replay, URL filters, generation API,
e — o maior — **o usuário final não vê os PRÓPRIOS logs** (só billing/keys).

### Activity (doc: features/activity)
- Tabs Overview | Trends | Explore | Guardrails, TODAS compartilhando
  date range + filtros (persistidos na URL).
- Overview: headline (spend/requests/tokens/custo-blended-1M/cache-hit),
  top API keys/apps/users, charts por modelo/tokens/caching/usage-type.
- Trends: séries temporais + card "Trending" (maiores movers vs período anterior).
- Explore: metric × group × subgroup × rollup, bar/line/dot, cumulative,
  séries >limite → "Other", configs salvos como named charts.
- Export CSV/PDF respeitando filtros.
- API: `/analytics/meta` + `POST /analytics/query` + `GET /activity` diário.

Nós temos: `/dashboard/activity` (feed de eventos), analytics/utilization,
cost explorer (group-by + barras). **Faltam**: tabs unificadas com estado
compartilhado, trending movers, export CSV, saved explores, analytics
query API, e **activity do usuário** (mesma visão scoped a ele).

### Pricing (doc: /pricing)
- Matriz de tiers: Free | Standard | Business | Enterprise — diferenciais:
  platform fee (5.5%/8%), budgets, rate limits (free=50 req/dia),
  management API, admin controls, BYOK, SSO.
- Para nós (modelo resale): **fee% sobre topup** (sobretaxa ao comprar
  crédito), **free tier** (quota diária sem saldo), tiers de plano por usuário.

## Specs propostas (por ordem de valor)

- **SPEC-141** `/account/activity` — o usuário vê os próprios logs+activity:
  tabela de generations dele (de usageHistory por userId/userKeyId), cards de
  spend/requests/tokens no range, chart por modelo, export CSV. É a peça que
  falta no "controla os gastos" do OpenRouter.
- **SPEC-142** `/dashboard/activity` upgrade — tabs Overview/Trends/Explore com
  date-range+filtro compartilhados na URL, trending movers, CSV export,
  guardrails tab (reusa SPEC-107 detections).
- **SPEC-143** generation detail sheet em `/dashboard/logs`: waterfall de
  tentativas (proxyLogs por correlationId), cost breakdown, raw JSON,
  Input&Output logging opt-in (settings.observability.ioLogging → guarda
  prompt/completion), `GET /api/v1/generation?id=` + `/generation/content`.
- **SPEC-144** plan tiers + platform fee: `settings.payments.feePct` soma na
  cobrança do checkout (usuário paga fee); free-tier daily cap por usuário sem
  saldo; bloco de tiers na página pública `/models`.

Prioridade sugerida: 141 → 143 → 142 → 144.
