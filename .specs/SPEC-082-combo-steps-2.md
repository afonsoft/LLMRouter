# SPEC-082 — Combo steps 2.0

Upstream `combos/steps.ts`, `invariants.ts`, `compositeTiers.ts`, `autoPromote.ts`, `modelNameCollision.ts`.

## Combo steps
- Step kinds: `model` (current), `combo-ref` (nested combo — expands referenced combo's pool at resolution, cycle-guard depth ≤4), `provider-wildcard` (`provider/*` — expands to all active-connection models of provider or all registry models).
- Step routing metadata preserved on normalize (weight, optional per-step overrides).
- `ModelResolver`/`ComboPlanner`: expand combo-ref + wildcard into flat candidate list before ordering.

## Validation & hygiene
- `invariants` — family validation (FAMILY_PATTERNS gpt/claude/gemini/glm/kimi/deepseek/qwen/llama/mistral/moonshot): `ComboInvariantError` on save when steps mix incompatible families unless `allowMixedFamilies` config flag.
- `compositeTiers` — validate tiered composite combo config (tiers array, each with steps + strategy; field-level errors).
- `modelNameCollision` — combo named identical to a bare model id: combo wins on resolution (combo-before-rewrite precedence); warn in API response when creating colliding name.

## autoPromote
- `settings.data.comboAutoPromoteEnabled` — on successful response, promote winning step to top of combo order (persisted); skip for sticky/random strategies per upstream.

## Tests (≥5)
- nested combo-ref expands + cycle guard; wildcard expands provider pool; family invariant rejects mixed combo; autoPromote reorders after success; collision: combo beats bare model id.
