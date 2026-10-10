# SPEC-084 — Routing brain: intelligentRouting + adaptiveRouting

Upstream `combos/intelligentRouting.ts` (16 weights + modePack + budgetCap) and `routing/adaptiveRouting.ts` (allow/warn/deny + explanations) + `intelligence/sync` (Arena ELO → quality).

## Intelligent routing (port fiel)
- Full `IntelligentRoutingWeights`: quota .25? per upstream defaults — quota, health, costInv, latencyInv, taskFit, stability, tierPriority, tierAffinity, specificityMatch, contextAffinity, cacheAffinity, sessionAvailability, resetWindowAffinity, connectionDensity, quality, reliability.
- `IntelligentRoutingConfig`: candidatePool, explorationRate, modePack, budgetCap, sla* — from `settings.data.autoRouter` + combo.config.
- Factor computation per candidate from live telemetry (breaker/quota/usageHistory/session pools/registry) — same math as upstream `scoreCandidate`.
- Kind `auto`/`lkgp` upgrade to full scoring; `rules/score/…` keep existing.

## Adaptive routing
- `AllocationDecision` allow/warn/deny per candidate (budget, breaker, quota signals); warn/deny candidates annotate routingDecisions; deny filtered before ordering.
- `RoutingExplanation`: score + eligible + reasons[] + factors{} — expose via `GET /api/routing/decisions/{requestId}` extended + `/api/routing/explain` dry-run.

## intelligence/sync
- `POST/GET/DELETE /api/intelligence/sync` — pull Arena ELO (or cached fixture when offline) into kv `intelligence:{provider}:{model}`; feeds `quality` weight. Fail-open.

## Tests (≥5)
- 16-factor scoring deterministic on fixture; deny allocation filters candidate; explanation carries reasons+factors; ELO sync feeds quality weight; exploration re-rolls head.
