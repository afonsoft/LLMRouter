# SPEC-081 — Routing small knobs

Upstream parity: `resilience/reset`, `free-models` budgets, `disabledModels` (9router), `comboSort`/`comboContext`, `deadConfigKeys`.

## Endpoints
- `POST /api/resilience/reset` — clear all ProviderBreaker + ModelLockout + CooldownTracker state (upstream: all breakers + model lockouts).
- `GET /api/free-models` — FREE_MODEL_BUDGETS catalog: provider, modelId, displayName, monthlyTokens, creditTokens, freeType, poolKey (source: upstream freeModels lib; seed from freeTier settings + registry free flags).
- `GET/PUT /api/models/disabled` (+ DELETE) — per-provider model disable list (9router `disabledModelsDb`): hidden from `/v1/models` and skipped in combo resolution (`{provider, model}` rows or kv `disabledModels:{provider}` JSON).
- `GET /api/combos/{id}/context-window` — effective combo context window (upstream `comboContext.ts`: min/max/weighted across member models from registry+overrides).
- `GET /api/settings/dead-config-keys` — list settings.data keys not in known schema (upstream `deadConfigKeys.ts`); `DELETE` to prune.

## Core
- `ResilienceReset` helper; `DisabledModels` store + check in ModelResolver/ComboPlanner and `/v1/models`; `ComboContext` context-window computation; `DeadConfigKeys` known-key set.

## Tests (≥4)
- reset clears breaker+lockout state; disabled model absent from /v1/models and skipped in combo; free-models returns catalog; context-window computes member bounds; dead keys listed+pruned.
