# SPEC-083 — Mappings & key scoping

Upstream `model-combo-mappings` (glob), `quota/keys/{id}/models` (`qtSd/`), 9router `keyAccess`.

## model-combo-mappings (upgrade)
- Rows: `{pattern, comboId, priority, enabled, description}` (new table `modelComboMappings`; migrate existing kv exact entries).
- Glob matching (`*`/`?`), ordered by priority desc then insertion; `resolveComboForModel(model)` in gateway hot path (HotReads cache).
- Endpoints CRUD `{id}`-based: GET/POST/PATCH/DELETE `/api/model-combo-mappings[/id]`.

## quota key model scoping
- `GET /api/quota/keys/{id}/models` — `qtSd/` virtual ids the key sees in `/v1/models`: resolveQuotaKeyScope → pool slugs → filter combos (upstream catalog.ts semantics).
- `/v1/models`: when caller key is quota-restricted, expose `qtSd/{pool}` virtual ids.

## keyAccess (9router)
- Restricted key allow-list accepts combo names AND provider-as-model entries (`{provider}` kind) — `AccessAllow` JSON entries support `{kind:"combo", value:name}` and `{kind:"provider-as-model", value:provider}` alongside current strings; denial message matches upstream.

## Tests (≥4)
- glob pattern routes `gpt-*` to combo; priority ordering; quota key sees only its pools in /v1/models; restricted key allowed via combo entry.
