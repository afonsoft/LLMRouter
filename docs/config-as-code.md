# Config-as-code (`llmr-config`)

SPEC-104 — export the whole routing/config surface as one versionable JSON
document (no secrets, no usage data). LiteLLM `config.yaml` / Kong decK style.

```json
{ "format": "llmr-config", "version": 1, "exportedAt": "…",
  "scopes": { "settings": {}, "combos": [], "mappings": [],
              "rateLimits": [], "keys": [], "providers": [], "guardrails": [] } }
```

## Endpoints

| Route | Notes |
|---|---|
| `GET /api/config/export?scope=` | `scope` = comma list or `all`. `providers` redacts secret keys (`apiKey`, `accessToken`, `token`, `secret`, `password`, `clientSecret`, `privateKey`, `refreshToken`) inside `data` as `{"$secret": true}` — files are commit-safe. |
| `POST /api/config/validate` | Schema-check: format/version, known scope keys, per-scope shapes, combo step references resolve (provider, wildcard, auto/*, or another combo). |
| `POST /api/config/diff` | Posted doc vs current state → per-scope `{added[], removed[], changed[]}` (id-based; provider secrets stripped before compare). |
| `POST /api/config/import` | `{doc, scopes?, dryRun?, redactedSecrets}` — validates first (errors abort), applies inside one transaction per scope, audits `config.import`. `redactedSecrets:"keep"` preserves stored secrets where the doc has `$secret` markers; `"clear"` drops them. `dryRun` returns counts, writes nothing. |

## Boot bootstrap

`LLMR_CONFIG_IMPORT=/path/to/config.json` applies a document once at startup
(`redactedSecrets: keep`) — headless-friendly, mirrors LiteLLM `config.yaml`.

Example: `docs/examples/llmr-config.json`.
