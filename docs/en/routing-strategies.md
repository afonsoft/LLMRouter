# Routing Strategies

A combo routes through `Combo.Kind`. Ordering strategies return a reordered
candidate list; execution strategies run at the endpoint.

## Ordering strategies (`ComboStrategies.OrderAsync`)

| Kind | Behavior |
|---|---|
| `fallback` (default) | declared order |
| `round-robin` | rotating head, `stickyLimit` requests each |
| `random` / `strict-random` | shuffle / single random pick |
| `lkgp` | last-good combo member first |
| `cache-optimized` | prompt-hash affinity to last winner |
| `weighted` | `provider/model~N` weight parse |
| `least-used` | lowest usageHistory count |
| `cost-optimized` | cheapest by observed cost |
| `p2c` | power-of-two-choices |
| `auto` | composite score (latency/error/quota) |
| `quota-weighted` | weighted-random by remaining quota |
| `headroom` | max remaining quota first |
| `reset-aware` | prefers soonest quota reset |
| `context-optimized` | fits request tokens to model context length |

## Auto-router strategies (SPEC-078, upstream `routerStrategy.ts`)

Candidates get live telemetry: breaker state, quota fraction, cost/1M tokens,
p95/avg latency + stddev, error rate (usageHistory). OPEN-breaker candidates
yield to healthy ones; all-OPEN returns pool unchanged.

| Kind | Scoring |
|---|---|
| `rules` | quota .25 / health .2 / cost .2 / latency .15 / reliability .1 / stability .1 |
| `score` | `rules` + `explorationRate` head re-roll |
| `cost` / `eco` | cheapest cost/1M first |
| `latency` / `fast` | e2e .45 / error .25 / stability .15 / breaker .15 |
| `sla-aware` / `sla` | latency .35 / error .35 / health .15 / cost .10 / stability .05; `hardConstraints` sorts violation score first (defaults p95 2000 ms, err 5 %) |
| `lkgp` | last successful provider (usageHistory) first, then rules — the combo-level `lkgp` kind stays separate |
| `nadir` | `POST {base}/v1/bucket` `{prompt≤16k, menu≤100, source:omniroute}` + `X-API-Key`; `selected_model` heads the list, rules orders the rest; fail-open on any error + 30 s per-baseURL cooldown. Config/env: `autoRouter.nadir.*`, `LLMR_NADIR_*`, `OMNIROUTE_NADIR_*` |

Config lives in `settings.data.autoRouter`.

## Execution strategies

| Kind | Behavior |
|---|---|
| `fusion` | parallel fan-out to all candidates + judge pick |
| `pipeline` | sequential transform steps |
| `vision-adapter` | routes vision requests through a vision-capable member |

## Shaping pre-pass (before ordering)

- `modelCooldowns` — provider+model in cooldown is skipped.
- `fallbackChains` — named chain replaces the raw pool order.
- `ProviderBreaker`/`ModelLockout`/`QuotaWindows`/`CooldownTracker` filter.
- `capabilityOverrides` + registry caps pre-filter (vision/tools/json/PDF).
- Every choice is journaled in `routingDecisions` (explanable via
  `GET /api/routing/decisions`).

## Virtual combos `auto/*`

`auto/best|coding|fast|free` and `auto/{substring}` resolve a live pool:
active connections × registry + synced catalogs, filtered by name pattern.
Usable directly as `model` in gateway requests without a persisted combo.
