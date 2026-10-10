# SPEC-078 — Auto-router strategies (upstream autoCombo/routerStrategy.ts + nadirStrategy.ts)

A combo whose `Kind` is an auto-router strategy routes its pool through telemetry-scored candidate routing instead of the simple ordering kinds.

## Strategies (combo `Kind`)

- `rules` — weighted composite: quota .25, health .2, cost .2, latency .15, reliability .1, stability .1 (upstream's scoring-engine role).
- `score` — same composite + `autoRouter.explorationRate` random head swap.
- `cost` / `eco` — cheapest by observed cost per 1M tokens (usageHistory cost/tokens).
- `latency` / `fast` — e2e latency .45, error rate .25, stability .15, breaker health .15.
- `sla-aware` / `sla` — faithful port of `SLAStrategyImpl`: latency .35 + error .35 + health .15 + cost .10 + stability .05; `hardConstraints` sorts by violation score first. Defaults p95 2000ms / err 5%.
- `lkgp` — last successful provider first (usageHistory), then rules.
- `nadir` — `POST {baseUrl}/v1/bucket` {prompt ≤16k chars, menu ≤100, source:"omniroute"} with `X-API-Key`; `selected_model` heads the list, rules orders the rest. Fail-open on any error + 30s per-baseURL cooldown; never 5xx.

## Config

`settings.data.autoRouter` = `{explorationRate, sla:{targetP95Ms,maxErrorRate,maxCostPer1MTokens,hardConstraints}, lkgp:{enabled}, nadir:{apiKey,baseUrl,timeoutMs}}`; nadir env fallbacks `LLMR_NADIR_*` and upstream-parity `OMNIROUTE_NADIR_*`.

## Candidates

Built per `provider/model` from live data: breaker state (`ProviderBreaker`), quota fraction (`QuotaWindows`), cost/latency p95/avg/stddev/error-rate (`usageHistory`). OPEN-breaker candidates yield to healthy ones (all-OPEN → pool unchanged, upstream semantics).

## Invariants

- Non-auto kinds untouched; `lkgp` keeps its combo-level last-good implementation (listed in `StrategyNames` for discovery but not rerouted).
- Nadir egress is minimal: last user turn only, no system prompt/history/tools.

## Tests

`tests/LLMRouter.Tests/AutoRouterTests.cs` — strategy ordering, SLA hard-constraints, lkgp, nadir prompt/URL helpers, kind routing.
