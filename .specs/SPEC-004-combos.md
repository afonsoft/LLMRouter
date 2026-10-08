# SPEC-004 — Combos advanced + Model aliases + Combo Studio

## Goal

Full combo feature set beyond SPEC-001 basics: round-robin with sticky limits UI,
vision adapter combos, model alias management, live combo studio page.

## Upstream sources

- `open-sse/services/combo.js` (rotation already ported in SPEC-001; this adds UI + kinds)
- `src/app/(dashboard)/dashboard/combos/**`, `.../combos/live/**` (Combo Studio flow view)
- `src/shared/components/{ComboFormModal,ModelRoutingSection,flow/**}`
- `src/app/api/combos/**`, `src/app/api/model-combo-mappings/**`
- 9router `src/app/(dashboard)/dashboard/combos/**` (simpler canonical version)

## Scope

- `combos.kind`: `fallback` (ordered cascade), `round-robin` (+`stickyLimit`),
  vision-adapter combos (kind=`vision-adapter`: chain imageToText model → llm).
- `model-combo-mappings`: map upstream-facing model names to combos (auto-wrap).
- Model aliases CRUD stored in `kv` scope `modelAliases`; participates in
  `ModelResolver` exactly like upstream `resolveModelAliasFromMap`.
- `/dashboard/combos`: list, create/edit modal (name, kind, ordered `provider/model`
  list with drag-reorder — dnd-kit upstream; in Blazor use up/down buttons or
  HTML5 drag), per-model capability badges, test button.
- `/dashboard/combos/live` (Combo Studio): live routing cascade view — shows which
  model served recent requests per combo (from `requestDetails`), current rotation
  index for round-robin, per-model success rate.
- `/dashboard/model-combo-mappings` or a section inside combos page.

## Acceptance criteria

- Round-robin combo serves consecutive requests from different models with
  sticky-limit honored; UI shows/edits strategy + stickyLimit.
- Vision-adapter combo: request with image → imageToText model extracts description
  → text injected into downstream llm request.
- Model alias `foo` → `provider/model` resolves through the gateway.

## Tests

- Round-robin + sticky across sequential calls; vision-adapter two-stage flow with
  fake upstreams; alias CRUD + resolution.
