# SPEC-089 — `/v1` leftovers (Responses extras, agents, relay bifrost, vscode mirrors)

Gap §3.1 remaining items of `docs/analysis/paridade-2026-10-10.md`.

## Responses API extras

- `POST /v1/responses/input_tokens` — count input tokens for a Responses-shaped
  request without executing it: reuse the tokenizer path from
  `responses` (tiktoken/approx — match what `count_tokens` does for Anthropic);
  returns `{input_tokens: n}`.
- `ANY /v1/responses/{*path}` — upstream catch-all passthrough for Responses
  sub-resources not yet modeled (e.g. `/responses/{id}/input_items`, future
  sub-paths): forward to the resolved provider with the Responses translator,
  preserving path + query. Only for paths not matching an explicit route.

## Model catch-all

- `GET /v1/models/{*path}` — `/v1/models/{p}` already covers one segment;
  catch-all handles nested ids like `openai/gpt-4o` (URL-encoded `openai%2Fgpt-4o`)
  returning the single-model card; 404 when no registry/synced match.

## Relay extras

- `POST /v1/relay/chat/completions/bifrost` — bifrost-protocol variant of the
  relay endpoint (upstream ships it for Bifrost-compat clients): same routing
  as `/api/relay/chat/completions` but request/response envelope is the bifrost
  shape `{provider, model, messages}` → `{choices, usage}` passthrough.

## Agents surface

Upstream `/v1/agents/*` for external agent runtimes:

- `GET /v1/agents/health` — `{ok:true, agents:[{id,status}]}` listing registered
  A2A agents + conductor health.
- `GET /v1/agents/tasks` + `GET /v1/agents/tasks/{id}` — read-only mirror of
  the A2A task list/task detail (`/api/a2a/tasks` data, OpenAI-shaped).
- `GET /v1/agents/credentials` — list api keys usable for agent auth
  (id+name+scopes, never the secret).

## Registered keys detail

- `GET /v1/registered-keys/{providerId}` — detail for one registered key entry.
- `POST /v1/registered-keys/{providerId}/revoke` — mark revoked (soft-delete:
  keep row, add `revokedAt`), revoked keys excluded from rotation picks.

## Batches housekeeping

- `POST /v1/batches/delete-completed` — delete kv `v1batches` entries with
  status completed/failed/cancelled older than `?olderThanDays` (default 0);
  returns `{deleted: n}`.

## vscode mirrors

Upstream mounts the whole vscode surface again under raw + combos prefixes:

- `/v1/vscode/raw/{token}/*` — identical to `/v1/vscode/{token}/*` but always
  raw provider passthrough (skip combo expansion).
- `/v1/vscode/combos/{token}/*` — identical but forces combo resolution even
  for bare model names.

## management/proxy-subscriptions + proxies detail

- `GET/POST /v1/management/proxies/assignments` — list/set proxy↔provider
  assignments (kv `proxyAssignments`).
- `POST /v1/management/proxies/bulk-assign` — body `[{proxyId,providerId}]`
  applying the whole set atomically.
- `GET /v1/management/proxies/health` — per-proxy health rollup
  (last check, latency, fail streak — reuse proxy test data).
- `GET/PUT /v1/management/proxy-subscriptions/{p}` + `/{p}/nodes` +
  `/{p}/refresh` — detail + node list + force-refresh of a subscription.

## Tests (≥10)

- responses/input_tokens counts ≈ Anthropic count_tokens for same payload;
- /v1/responses/{unknown-subpath} forwards preserving path+query;
- models/openai%2Fgpt-4o resolves nested id, unknown → 404;
- relay bifrost envelope round-trip through a stub provider;
- agents/health + tasks reflect seeded A2A tasks;
- registered-keys/{p}/revoke excludes key from subsequent rotation;
- batches/delete-completed prunes only terminal entries;
- vscode/raw/{token}/chat/completions bypasses combos (model sent verbatim);
- vscode/combos/{token}/... resolves combo name;
- proxies bulk-assign + health rollup;
- proxy-subscriptions/{p}/refresh updates node rows.
