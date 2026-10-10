# SPEC-092 — Management batch (guardrails, MCP mgmt, quota, keys, webhooks, pricing, models)

Gap §3.2 mgmt group of `docs/analysis/paridade-2026-10-10.md`. Missing
management APIs behind already-shipped surfaces.

## Guardrails API

`PromptGuard` exists in `Core/Guardrails` but has no CRUD/test API — config is
only reachable via the settings blob.

- `GET/POST /api/guardrails` + `GET/PUT/DELETE /api/guardrails/{id}` — rules:
  `{id, name, kind(regex|keyword|length|json-schema), pattern, action(block|warn|redact), target(request|response|both), enabled}`,
  persisted as `guardrails` table.
- `POST /api/guardrails/test` — `{input, target}` → `{matches:[{ruleId,name,action}], wouldBlock:bool}`.
- Wire into `GatewayEndpoints` request pipeline (before dispatch) and
  passthrough responses — actions run in order block→warn→redact.
- Settings `guardrails.enabled` master switch (default on; empty table = no-op).

## MCP management

Server runs at `/mcp` + `/mcp/sse`; upstream adds operator controls:

- `GET /api/mcp/status` — `{running, transport, tools:32, connectedClients,
  lastRequest}`.
- `GET /api/mcp/tools` — tool catalog (names + descriptions + which combo the
  `chat__*` ones map to).
- `POST /api/mcp/restart` — reset MCP session state/sse subscribers
  (idempotent; returns `{restarted:true}`).
- `GET /api/mcp/stream` — SSE stream of MCP request/response audit events
  (existing `mcpAudit` rows pushed live).

## Quota groups + pools

`quota windows/schedules/plans` exist; upstream also groups quotas:

- `GET/POST /api/quota/groups` + `GET/PUT/DELETE /api/quota/groups/{id}` —
  `{id, name, providerIds[], windows[]}`.
- `GET/POST /api/quota/pools` + `GET/PUT/DELETE /api/quota/pools/{id}` —
  named pools of quota entries with `{capacity, used, resetAt}`.
- `GET /api/quota/keys/{providerId}/models` — which models a quota key covers.

## API keys sub-resources

- `GET/POST /api/keys/groups/{id}/keys` — assign keys to a key group.
- `GET/PUT /api/keys/groups/{id}/permissions` — per-group permission set
  (`{scopes:[chat,embeddings,media,...]}`); enforcement in `V1Authed`.
- `GET /api/keys/{id}/access` — access log for the key (from usageHistory
  keyed rows: last N calls).
- `GET /api/keys/{id}/devices` — devices/IPs seen using the key
  (distinct client fingerprints from requestDetails).

## Webhooks detail

- `GET/PUT/DELETE /api/webhooks/{id}` — single webhook ops (list/create exist).
- `GET /api/webhooks/{id}/deliveries` — delivery attempts for that webhook.
- `POST /api/webhooks/{id}/test` — fire a test event → returns delivery result.
- `POST /api/webhooks/validate-url` — `{url}` → SSRF-guard validation
  `{valid, reason}` (block loopback/RFC1918 unless `webhooks.allowPrivate`).

## Pricing extras

- `GET/PUT /api/pricing/defaults` — default price rules (per-1M-token fallback).
- `GET /api/pricing/models` — merged price table (defaults + overrides).
- `POST /api/pricing/sync` — re-pull prices from models.dev catalog (stub-safe:
  no network in tests; accept injected fixture).

## Models extras

- `GET/POST/PUT/DELETE /api/models/alias*` — model alias CRUD (`aliasModels`
  kv/table — alias → `provider/model`); used by model resolution before
  combo expansion.
- `GET /api/models/catalog` — full catalog view: registry + synced + aliases.
- `GET /api/models/openrouter-catalog` — openrouter-shaped catalog dump.
- `POST /api/models/test` — `{providerId,model}` → single test call result.
- `POST /api/models/test-all` — fan-out test to all enabled providers' default
  model → `{results:[{provider,ok,latencyMs,error}]}`.

## Tests (≥12)

- guardrails CRUD + /test matches; block action rejects a seeded request;
- redact action strips pattern from upstream response;
- mcp/status + tools list; restart resets sse subscribers;
- quota groups+pools CRUD round-trip;
- keys/groups permissions enforced (key without `embeddings` scope → 403 on
  /v1/embeddings);
- keys/{id}/access returns last calls for that key;
- webhooks/{id}/test fires delivery, validate-url blocks 127.0.0.1;
- pricing defaults+models merge; sync with injected catalog fixture;
- models/alias resolves through model resolution path;
- models/test-all returns per-provider results over stub registry.
