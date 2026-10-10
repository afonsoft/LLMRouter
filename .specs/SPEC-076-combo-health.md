# SPEC-076 — Combo health: stale-step flag/prune, typed availability, quota-scoped scoring

Ports upstream 3.8.52 deltas: `providerAvailability.ts` (#15918), `staleModelRefs`/`staleModelPrune` (#13505 + #15925), quota-weighted scoring scoped to the requested model's windows (#16054).

## Scope

1. **Typed provider availability** — `Core/Resilience/ProviderAvailability.cs`, pure classifier faithful to upstream: `AVAILABLE | NO_CREDENTIAL | AUTH_EXPIRED{REAUTHENTICATE} | QUOTA_EXHAUSTED{nextEligibleRecheckAt} | DISABLED | STALE_TERMINAL{previousState} | UNHEALTHY{retryable}`. Terminal statuses (`credits_exhausted`, `banned`, `expired`) trusted only when confirmed within 24h, else `STALE_TERMINAL` — never silently back to `AVAILABLE`. Exposed as `availability` on `GET /api/health/connections`; a live `QuotaTracker` exhaustion is fresh `credits_exhausted` evidence.

2. **Stale combo model refs** — `Core/Routing/StaleComboRefs.cs`: after a successful `/api/synced-available-models` sync, explicit `provider/model` combo steps absent from (or `Available=false` in) the synced catalog are flagged in the response (`staleModelRefs`). Setting `settings.data.comboAutoPruneStaleSteps=true` (default OFF, matching upstream flag) prunes them — a combo whose every step is stale is left alone; pruning never empties a combo; each prune is audited `combo.stale_model_ref.pruned`. Failed/degraded syncs flag nothing (empty catalog → no refs).

3. **Quota-scoped scoring** — `quota-weighted`/`headroom` now score each model by the minimum remaining fraction (0..1) over the model's own provider quota windows + each active connection's configured limits (upstream scores by remaining percentage scoped to the requested model's windows). `QuotaWindows.RemainingFractionAsync` added.

## Invariants

- No behavior change when no windows/limits exist (score 1 → stable order).
- Auto-prune is opt-in; flagging is read-only and never fails the sync.
- Prune is atomic per combo and preserves combo order of remaining steps.

## Tests

`tests/LLMRouter.Tests/ComboHealthTests.cs` — classifier states, stale detection/prune edge cases, window-scoped headroom ordering, exhausted-window fraction.
