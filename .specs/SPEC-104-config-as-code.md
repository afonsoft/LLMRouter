# SPEC-104 — Config-as-code: export/import/diff (`llmr-config`)

Origin: `comparativo-mercado` §8.4 — settings live only in SQLite; LiteLLM has
`config.yaml`, Kong decK, Envoy CRDs. Give operators a versionable document
covering the routing/config surface (not secrets, not usage data).

## Work

- `GET /api/config/export?scope=` — scopes `settings|combos|mappings|rateLimits|
  keys|providers|guardrails|all`. Emits one JSON document:
  `{format: "llmr-config", version: 1, exportedAt, scopes: {...}}`.
  `providers` scope exports connections **without** `data.apiKey`/secrets —
  redacted as `{"$secret": true}` so files are commit-safe.
- `POST /api/config/validate` — schema-check a posted document (scope shapes,
  required fields, combo step references resolve); returns per-scope errors.
- `POST /api/config/diff` — posted document vs current state → per-scope
  `{added[], removed[], changed[]}` summaries (id-based, no writes).
- `POST /api/config/import` — `{doc, scopes?, dryRun?, redactedSecrets:
  "keep"|"clear"}`: validate → apply inside one transaction per scope;
  `"keep"` preserves existing secret fields when the incoming value is
  `$secret`-redacted. Audit `config.import` event with scope counts.
- `llmr-config` doc in `docs/` + example file under `docs/examples/`.
- Optional `LLMR_CONFIG_IMPORT` env var at boot: apply file once (headless
  friendly — mirrors LiteLLM `config.yaml` boot).

## Tests (≥5)

- export→import round-trip is idempotent (export, wipe scope rows, import,
  re-export byte-equal modulo timestamps);
- validate rejects unknown scope keys + dangling combo model refs;
- import with `dryRun` writes nothing and reports diff;
- `redactedSecrets:"keep"` does not clobber stored apiKey;
- boot-env import applies file when `LLMR_CONFIG_IMPORT` set.
