# Architecture

LLMRouter is a single deployable unit: an ASP.NET Core server that hosts the
gateway, the management API and the Blazor WebAssembly dashboard, backed by a
single embedded SQLite database. Designed to run in docker with zero external
dependencies.

## Projects

| Project | Role |
|---|---|
| `src/LLMRouter.Core` | Engine: provider registry, routing strategies, format translators, gateway pipeline, resilience, compression, jobs, OAuth |
| `src/LLMRouter.Server` | HTTP host: static WASM, `/v1/*` gateway endpoints, `/api/*` management endpoints, MITM surface (dev) |
| `src/LLMRouter.Client` | Blazor WASM dashboard (~65 pages, upstream layout, en/pt-BR/es) |
| `src/LLMRouter.Shared` | Shared DTOs |
| `tests/LLMRouter.Tests` | xUnit + Shouldly + WebApplicationFactory (serial suite) |

## Request path (gateway)

```
POST /v1/chat/completions  (Bearer sk-*)
  → auth: apiKeys lookup (+ per-key rules/limits)
  → model resolution: explicit "provider/model" | combo | auto/* pool
  → ComboPlanner: capability pre-filter (vision/tools/…), context-length
  → ComboStrategies.OrderAsync(kind) — ordering or auto-router strategies
  → per-candidate: ProviderBreaker.CanExecute, ModelLockout, QuotaWindows,
    CooldownTracker, connection selection
  → GatewayPipeline: auth headers + connection customHeaders, URL build
  → upstream call (SSE streaming or buffered), translation to request format
  → failure → next candidate (cooldown/lockout recorded); 4xx stops cascade
  → telemetry → UsageWriter channel → usageHistory/requestDetails (off-path)
```

Config reads (~15 per request) go through `HotCache`/`HotReads` (TTL +
invalidation on any non-telemetry write). Telemetry writes are serialized by
`UsageWriter` (bounded channel + `BackgroundService`); `LLMR_SYNC_WRITES=1`
runs them inline (tests).

## Routing stack

- **Ordering strategies** (`Combo.Kind`): fallback, round-robin, random,
  strict-random, lkgp, cache-optimized, weighted, least-used, cost-optimized,
  p2c, auto, quota-weighted, headroom, reset-aware, context-optimized.
- **Auto-router strategies**: rules, score, cost/eco, latency/fast, sla-aware,
  lkgp (provider-level), nadir (external decision API, fail-open).
- **Execution strategies** (endpoint-level): fusion (fan-out + judge),
  pipeline (sequential transforms), vision-adapter.
- **Fallback chains** and **model cooldowns** shape the candidate order before
  the strategy runs; every pick records a routing decision row.
- `auto/*` virtual combos resolve a live pool (active connections ×
  registry/synced catalog) by name — `auto/best|coding|fast|free` presets.

## Data model (SQLite, upstream-mirrored names)

`providerConnections`, `apiKeys`, `combos`, `usageHistory`, `requestDetails`,
`settings`, `kv`, `syncedModels`, `modelCapabilityOverrides`,
`modelCooldowns`, `fallbackChains`, `routingDecisions`, `quotaWindows`,
`providerNodes`, `compressionCombos/Assignments/Runs`, `files`, `evals`,
`chatSessions`, `keyGroups/quotaPlans/quotaSchedules`,
`logExportDestinations`, `jobStates/jobRuns`, `cliTokens`, `policies`,
`tags`, `sessions`, `credentialExpirations`, `a2aTasks`, `conductorFleet`.

## Resilience

- `ProviderBreaker`: Closed → Degraded → Open → HalfOpen; herd-window dedup.
- `CooldownTracker` (provider) + `ModelLockout` (provider+model scoped).
- `QuotaWindows`: sliding request/token windows per provider; enforced on
  resolve, scored by auto strategies.
- `ProviderAvailability`: typed states (`AVAILABLE`, `NO_CREDENTIAL`,
  `AUTH_EXPIRED`, `QUOTA_EXHAUSTED`, `DISABLED`, `STALE_TERMINAL`,
  `UNHEALTHY`) — terminal quota errors trusted only ≤24 h (freshness).
- `StaleComboRefs`: flags combo steps absent from the synced catalog; opt-in
  auto-prune (never empties a combo).
- `ErrorSanitizer`: upstream `toJsonErrorPayload` port — field allow-list,
  ~20 credential patterns, stack/path/URL redaction before errors reach the
  client.

## Compression

`Core/Compression`: `ICompressionEngine` + `CompressionRegistry` +
`CompressionPipeline` (combo-assignment > stackedPipeline > comboOverrides >
defaultMode), `CompressionEndpoints` + `compressionRuns` telemetry. Engines:
lite, session-dedup, ccr, headroom, caveman (35 EN rules), aggressive, ultra,
rtk + fail-open stubs (llmlingua, omniglyph).
