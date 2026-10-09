# SPEC-043 — Playground extras

Upstream: `playground/{improve-prompt,presets(+id),simulate-route}`.
Today the playground is a straight chat box.

## Scope
- `POST /api/playground/improve-prompt` `{prompt,model?}` — asks a configured
  provider to rewrite the prompt (system: prompt-engineer template); returns
  `{improved}`.
- `playgroundPresets` table + CRUD `/api/playground/presets` — save
  model+params+prompt templates.
- `POST /api/playground/simulate-route` `{model,body?}` — dry-run through
  NodeResolver/combo logic; returns chosen provider/connection + fallback
  chain + would-be headers, without calling upstream.
- UI: extend `Playground.razor` — preset dropdown+save, "Improve" button,
  "Route" dry-run panel showing resolution steps.
- Tests: simulate-route returns provider for a model mapping; presets CRUD.
