# Auditoria de paridade de UI — LLMRouter vs OmniRoute (2026-10-11)

Metodologia: OmniRoute v3.8.52 de produção (https://omniroute.afonsoft.dev, DB real do usuário)
vs LLMRouter `main` local servindo o DB migrado do OmniRoute (868MB → 114MB, mesmos dados).
Screenshot das 95 rotas do sidebar em cada app, viewport 1440×900, diff RMSE por par.
Prints: sessão Devin 8f0d6217 (diretório /tmp/audit/{omni,llmr}).

## Cobertura estrutural

- LLMRouter expõe **154** rotas `@page`; OmniRoute expõe **95** rotas de sidebar.
- Rotas upstream sem rota dedicada no LLMRouter: apenas as 12 sub-rotas
  `/dashboard/settings/*` — o LLMRouter consolida settings em uma página com abas
  (General, Appearance, AI, Modality bridge, Routing, Resilience, Advanced,
  Security, Access tokens, Feature flags, Cache, Sidebar, Backup).
- Logo, a migração de estrutura de navegação está completa; os gaps são de
  **conteúdo, layout e funcionalidade dentro de cada tela**.

## Achados por tela (ordenado por divergência)

| Tela | Paridade | Deltas principais |
|---|---|---|
| `/docs` | MUITO BAIXA | Upstream: hub de docs dark com árvore categorizada (Getting Started/Guides/Architecture/Management/Reference/Routing/…), busca Ctrl+K, cards Quick Start/Auto-Combo/Providers, seções For Non-Tech / For Tech Users. Nosso: lista plana de arquivos .md numa coluna. |
| `/dashboard/logs/console` | BAIXA | Upstream: painel estilo terminal escuro com badges de nível coloridos, filtros por nível/fonte, contador de entradas, janela de tempo, "See more logs". Nosso: `<pre>` claro com as últimas 500 linhas, sem filtros. |
| `/dashboard/profile` | BAIXA | Upstream: perfil de gamificação (nível Explorer, XP, streak 11-day, grade de 23 badges com estados). Nosso `/dashboard/profile` renderiza "OAuth tokens" (Start OAuth + tabela de env vars) — conteúdo trocado. |
| `/dashboard/activity` | BAIXA | Upstream: feed de eventos com chips de categoria (Providers/Combos/API Keys/Settings/Quota/Auth/System), agrupamento por dia, timestamps relativos ("just now", "6 days ago"), ícones por tipo. Nosso: tabela Time/Provider/Model/Status. |
| `/dashboard/api-manager` | BAIXA | Upstream: seção "API key routing" (editor de regras model/reasoning), grade de cards de chave com chips de permissão/uso, chips de filtro (All/Active/Disabled/Bound/Expired/Free/Standard/Managed/Provisioned), busca, toggle "Active only". Nosso: tabela simples + "Key groups". |
| `/dashboard/analytics/utilization` | BAIXA | Upstream: gráfico de série temporal 24h empilhado por provider + cards por provider com % de utilização e barra de quota. Nosso: tabela com barra inline. |
| `/dashboard/analytics/combo-health` | BAIXA | Upstream: "Combo Health Autopilot" — cards de resumo (689 issues/13 degraded), alertas HIGH acionáveis ("success rate below target", "quota exhausted", "terminal account state") com botões Run cookbook/Open combo editor. Nosso: tabela combo/score. |
| `/dashboard/providers` | MÉDIA | Nosso grid de tiles é parecido, mas: labels de categoria são enums crus (`APIKEY_PROVIDERS`, `OAUTH_PROVIDERS`…) em vez de texto amigável; logos de alguns providers estouram o tile (Dahl, OpenCode Zen); upstream agrupa em seções "API Key Compatible" / "OAuth Providers" com linhas expansíveis, chips de status (All/Connected/Error) e contadores. |
| `/dashboard/logs` (request logs) | MÉDIA | Upstream: toggle Recording/Pipeline, busca por modelo/provider/account/API key/correlation-id, chips de provider coloridos, filtros de tempo (1D/7D/30D/1h), colunas STATUS/LOGGED/MODEL/REQUESTED/TYPE/API KEY/ENV, linhas expansíveis, Clean history/Export. Nosso: tabela Time/Provider/Model/Status + dropdowns + CSV/JSONL. |
| `/dashboard/costs` | MÉDIA | Upstream: seletor de período (7/30/90/180/365/all), 4 cards (today/7d/30d/projected), stat row (requests, providers ativos, modelos, avg cost/req), "Cost Explorer" agrupável por Provider/Model/API key/Account/Service. Nosso: 3 números + tabelas por provider e top-modelos. |
| `/dashboard/costs/pricing` | MÉDIA | Upstream: cards (471 providers / 17342 registry / 12759 priced / 0 overrides), toggle "Pricing sync" + Clear Synced Pricing, busca, chips de cobertura, linhas de provider expansíveis com barras de cobertura. Nosso: tabela Input$/1M, Output$/1M, Overrides. |
| `/dashboard/costs/quota-share` | MÉDIA | Upstream: banner de aviso, "How Quota Share works", endpoints disponíveis (BASE URL + exemplo de request), preview por key, stats, empty state ilustrado. Nosso: tabela de pools 24h + card "Quota plans" vazio. |
| `/dashboard/quota` (Provider Quota) | MÉDIA | Upstream: cards Total/Critical/Alert/Healthy, chips por tipo (Subscription/OAuth Free/API Key) e tier, auto-refresh countdown, cards por conta com barras de quota por modelo + ações Edit limits/100 Cost/Refresh. Nosso: tabela Provider/Connection/Daily/Monthly/Status/Limit. |
| `/dashboard/conversations` | MÉDIA | Upstream: tabela real (conversation/turns/cost/tokens/last model/provider/status/last seen + filtros). Nosso: vazio "No saved conversations". |
| `/dashboard/health` | MÉDIA | Upstream: banner "OmniRoute is ready / All systems operational", diagnostics (DB health + Run Auto-Repair), cards Uptime/Version/Memory/Providers, Session Activity, Quota Monitors. Nosso: só a tabela Connection health. |
| `/dashboard/leaderboard` | MÉDIA | Upstream: tabs Overall/Weekly/Monthly/Tokens Shared, 3 stat cards, tabela ranqueada. Nosso: um card com top modelos. |
| `/dashboard/cli-agents` | MÉDIA | Upstream: detecção de CLIs instaladas, categorias (Code tools / autonomous agent / CLI used as backend), cards com status. Nosso: grade de cards com blocos de env var + Copy (conteúdo idêntico ao /cli-code — páginas duplicadas). |
| `/dashboard/cli-code` | MÉDIA | Upstream: profile auto-sync (checkboxes Codex/Claude), "CLI Code's", "CLI companion". Nosso: mesma grade de env vars (duplicata de cli-agents). |
| `/dashboard/models` | MÉDIA | Upstream: busca por modelo/provider/capability, dropdowns All Providers/All types, colunas Provider(icon)/Model/Type/Capabilities/Context/Output/Flags. Nosso: tabs Search models/Combos, colunas diferentes. |
| `/dashboard/logs/proxy` | MÉDIA | Upstream: request-log de proxies (recording toggle, contadores OK/ERR, chips, tabela com pills). Nosso: cards "Proxy pools"/"OneProxy" — feature diferente. |
| `/dashboard/combos` | ALTA-MÉDIA | Upstream: catálogo "Auto-routing" embutido, card onboarding 3 passos, banners de alerta, tabs All/Intelligent/Deterministic + busca. Nosso: tabs Combos/Models allowed/Models/Denied + Defaults + cards por combo (Test/Live/Edit/Del). |
| `/dashboard/home` | ALTA | Ambos: boas-vindas + stats + recent requests. Upstream adiciona sponsor banners (skip — são ads deles), "Quick Start" em passos numerados com links, "Provider Topology". Nosso: Quick Start 3 passos + stat cards. Pequenos deltas. |
| demais telas (~70) | ALTA | Mesma casca (sidebar idêntica, header, tabelas/cards). Deltas cosméticos ou de campos; listados por tela nas specs. |

## Specs de migração

Uma spec por cluster de telas (arquivos `.specs/SPEC-1xx-*.md`), cada uma com
checklist por tela. Ordem sugerida = impacto visual × uso:

1. SPEC-113 Activity feed · 2. SPEC-114 API Keys · 3. SPEC-115 Providers ·
4. SPEC-116 Logs (request/console/proxy/timeline) · 5. SPEC-117 Analytics
(utilization/combo-health/evals/search) · 6. SPEC-118 Costs (overview/pricing/
budget/quota-share) · 7. SPEC-119 Combos + Models catalog · 8. SPEC-120 Health +
Leaderboard · 9. SPEC-121 Profile/CLI pages (inclui fix do /profile trocado e da
duplicata cli-agents/cli-code) · 10. SPEC-122 Docs hub · 11. SPEC-123 Settings
sub-rotas (opcional — tabs já cobrem) · 12. SPEC-124 Home finishing touches.
