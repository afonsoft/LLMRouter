# Arquitetura

LLMRouter é uma unidade única de deploy: um servidor ASP.NET Core que hospeda
o gateway, a management API e o dashboard Blazor WebAssembly, apoiado por um
único SQLite embutido. Projetado para rodar em docker sem dependências
externas.

## Projetos

| Projeto | Papel |
|---|---|
| `src/LLMRouter.Core` | Engine: registry de providers, estratégias de roteamento, translators de formato, pipeline do gateway, resiliência, compressão, jobs, OAuth |
| `src/LLMRouter.Server` | Host HTTP: WASM estático, endpoints do gateway `/v1/*`, management `/api/*`, superfície MITM (dev) |
| `src/LLMRouter.Client` | Dashboard Blazor WASM (~65 páginas, layout upstream, en/pt-BR/es) |
| `src/LLMRouter.Shared` | DTOs compartilhados |
| `tests/LLMRouter.Tests` | xUnit + Shouldly + WebApplicationFactory (suíte serial) |

## Caminho do request (gateway)

```
POST /v1/chat/completions  (Bearer sk-*)
  → auth: lookup em apiKeys (+ regras/limites por key)
  → resolução do modelo: "provider/model" explícito | combo | pool auto/*
  → ComboPlanner: pré-filtro por capacidade (visão/tools/…), context length
  → ComboStrategies.OrderAsync(kind) — ordenação ou auto-router
  → por candidato: ProviderBreaker.CanExecute, ModelLockout, QuotaWindows,
    CooldownTracker, seleção de conexão
  → GatewayPipeline: headers de auth + customHeaders da conexão, URL
  → chamada upstream (SSE streaming ou buffered), tradução pro formato do request
  → falha → próximo candidato (cooldown/lockout registrado); 4xx para a cascata
  → telemetria → canal UsageWriter → usageHistory/requestDetails (fora do path)
```

Leituras de config (~15 por request) passam por `HotCache`/`HotReads` (TTL +
invalidação em qualquer escrita não-telemetria). Escritas de telemetria são
serializadas pelo `UsageWriter` (channel bounded + `BackgroundService`);
`LLMR_SYNC_WRITES=1` as executa inline (testes).

## Stack de roteamento

- **Estratégias de ordenação** (`Combo.Kind`): fallback, round-robin, random,
  strict-random, lkgp, cache-optimized, weighted, least-used, cost-optimized,
  p2c, auto, quota-weighted, headroom, reset-aware, context-optimized.
- **Auto-router**: rules, score, cost/eco, latency/fast, sla-aware, lkgp
  (nível provider), nadir (API externa de decisão, fail-open).
- **Estratégias de execução** (endpoint): fusion (fan-out + juiz),
  pipeline (transformações sequenciais), vision-adapter.
- **Fallback chains** e **model cooldowns** moldam a ordem dos candidatos
  antes da estratégia; cada escolha grava uma linha de decisão.
- Combos virtuais `auto/*` resolvem pool ao vivo (conexões ativas ×
  registry/catálogo sincronizado) — `auto/best|coding|fast|free`.

## Modelo de dados (SQLite, nomes espelhando o upstream)

`providerConnections`, `apiKeys`, `combos`, `usageHistory`, `requestDetails`,
`settings`, `kv`, `syncedModels`, `modelCapabilityOverrides`,
`modelCooldowns`, `fallbackChains`, `routingDecisions`, `quotaWindows`,
`providerNodes`, `compressionCombos/Assignments/Runs`, `files`, `evals`,
`chatSessions`, `keyGroups/quotaPlans/quotaSchedules`,
`logExportDestinations`, `jobStates/jobRuns`, `cliTokens`, `policies`,
`tags`, `sessions`, `credentialExpirations`, `a2aTasks`, `conductorFleet`.

## Resiliência

- `ProviderBreaker`: Closed → Degraded → Open → HalfOpen; dedup de herd-window.
- `CooldownTracker` (provider) + `ModelLockout` (escopo provider+modelo).
- `QuotaWindows`: janelas deslizantes de requests/tokens por provider;
  enforcement no resolve e scoring pelas estratégias auto.
- `ProviderAvailability`: estados tipados (`AVAILABLE`, `NO_CREDENTIAL`,
  `AUTH_EXPIRED`, `QUOTA_EXHAUSTED`, `DISABLED`, `STALE_TERMINAL`,
  `UNHEALTHY`) — erros terminais de quota confiáveis só até 24h.
- `StaleComboRefs`: marca steps de combo ausentes do catálogo sincronizado;
  auto-prune opt-in (nunca esvazia o combo).
- `ErrorSanitizer`: port do `toJsonErrorPayload` — allow-list de campos,
  ~20 padrões de credencial, redação de stack/path/URL antes do erro chegar
  ao cliente.

## Compressão

`Core/Compression`: `ICompressionEngine` + `CompressionRegistry` +
`CompressionPipeline` (combo-assignment > stackedPipeline > comboOverrides >
defaultMode), `CompressionEndpoints` + telemetria `compressionRuns`. Engines:
lite, session-dedup, ccr, headroom, caveman (35 regras EN), aggressive, ultra,
rtk + stubs fail-open (llmlingua, omniglyph).
