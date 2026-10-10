# SPEC-095 — Medium features batch (agent-skills/acp backends, memory items, modality-bridge)

Gap §3.2 medium group of `docs/analysis/paridade-2026-10-10.md`. Pages exist for
most of these — the dedicated backends are missing.

## Agent-skills backend

`/dashboard/agent-skills` currently reuses `/api/skills`. Upstream has a
dedicated `/api/agent-skills` domain:

- `GET/POST /api/agent-skills` + `GET/PUT/DELETE /api/agent-skills/{id}` —
  agent-scoped skill records `{id, agentId, skillRef, enabled, config}`.
- `GET /api/agent-skills/coverage` — which agents have which skills
  (`[{agentId, skills:[...]}]`).
- `POST /api/agent-skills/generate` — generate a skill stub for an agent
  (`{agentId, name, description}` → draft SKILL.md content stored as item).
- `GET /api/agent-skills/{id}/raw` — raw SKILL.md/artifact content.
- Point the page at the new endpoints (keep `/dashboard/agent-skills` route).

## ACP agents + well-known

- `GET/POST /api/acp/agents` + `GET/PUT/DELETE /api/acp/agents/{id}` — ACP
  (Agent Communication Protocol) agent registry `{id, name, endpoint, caps,
  enabled}`; `/dashboard/acp-agents` consumes it (today it aliases `/api/a2a`).
- `GET /.well-known/agent-card.json` — A2A agent card: `{name, url, skills,
  capabilities}` generated from the registry (upstream shape); the existing
  `agent.json` stays.

## Modality bridge

Upstream `/api/modality-bridge/*` wraps media capability bridging:

- `GET /api/modality-bridge/stats` — bridge usage counters (per modality:
  audio/video/image/ocr).
- `POST /api/modality-bridge/video/extract` — extract audio/frames from a
  video generation result (bridge video→media pipeline; accept `{jobId|url}`).
- `GET /api/modality-bridge/video/drilldown` — drilldown info for a video job
  (extends the existing `/v1/video-bridge/drilldown` data with status/meta).
- `GET /api/modality-bridge/video/runtime` — runtime status of the video
  pipeline (workers, queue depth — stub `{workers:0}` acceptable if no
  background worker exists yet; keep the route + honest payload).

## Memory item ops

`kv` memory scope exists; upstream exposes item-level routes:

- `GET /api/memory/{id}` + `PUT /api/memory/{id}` + `DELETE /api/memory/{id}` —
  single memory item ops (SPEC-087 partitioned memory into rows — use the
  `memoryItems` table when present, kv blob otherwise).
- Keep existing collection + engine endpoints untouched.

## Conversations tree

- `GET /api/conversations/{id}/tree` — conversation message tree
  (`{id, root, nodes:[{id,parentId,role,content,at}]}`) for threaded chat
  exports; flat conversations serialize as depth-1 trees.

## Translator extras

- `POST /api/translator/send` — send through the translator pipeline end-to-end
  (`{from, to, payload}` → translated request + upstream response).
- `POST /api/translator/translate` — payload-only translation preview
  (no upstream call).
- `POST /api/translator/transform-stream` — SSE transform preview: feed a
  canned SSE stream, get the translated frames back (for debugging
  translators without a provider).

## Plugins detail ops

Plugin CRUD + toggle exist; upstream adds lifecycle:

- `POST /api/plugins/{id}/activate` + `/deactivate` — lifecycle states
  (activate = load hooks into the pipeline; deactivate = unload without
  disabling config).
- `GET/PUT /api/plugins/{id}/config` — per-plugin config blob.
- `POST /api/plugins/scan` — rescan plugin dir/marketplace for new plugins.
- `POST /api/plugins/marketplace/install` — install from marketplace ref.

## Misc detail ops

- `GET /api/skills/{id}` — single skill detail (page needs it).
- `GET /api/combos/metrics` — per-combo metrics (alias of combo-health rows
  for the metrics page).
- `GET/PUT /api/context/combos/default` — default context-combo binding.
- `GET /api/compression/compare/verify` — verify a stored comparison result.
- `GET /api/evals` + `GET /api/evals/{id}` — flat evals list/detail (suites
  model already exists — flat view joins suites+runs).
- `GET /api/db-backups` — list backups found under the backups dir/table.
- `GET /api/docs/codex-cli` — codex-cli setup doc (markdown payload).
- `GET /docs/api/search` — full-text search over the docs catalog.
- `GET /api/analytics/auto-routing` + `/api/analytics/diversity` — routing
  analytics views (auto-router decisions log + provider diversity index).
- `GET /api/init` — server init state (`{initialized, version, checks}`).
- `GET /api/assess` — server self-assessment rollup.
- `GET /api/rate-limit` — current rate-limit config/state dump.
- `GET /api/resilience` + `/api/resilience/connections` — index + per-connection
  resilience states.
- `GET /api/system/version` — version payload parity (`{version, build, sha}`).
- `GET /api/provider-metrics` — registry-wide metrics rollup.
- `POST /api/provider-models/validate-and-add` — validate model exists upstream
  then add to syncedModels.
- `POST /api/provider-nodes/validate` — node URL validation (SSRF-guarded).
- `GET /api/free-tier/summary` — free-tier usage rollup.

## Tests (≥12)

- agent-skills CRUD + coverage + generate + raw;
- acp agents CRUD; agent-card.json shape;
- modality-bridge stats + runtime payload;
- memory item get/put/delete round-trip;
- conversations/{id}/tree for a seeded thread;
- translator send/translate/transform-stream over a stub;
- plugins activate/deactivate/config round-trip;
- skills/{id}, combos/metrics, evals flat list;
- db-backups list; docs/api/search hits a seeded doc;
- analytics endpoints return computed values;
- init/assess/rate-limit/resilience/system-version payloads;
- provider-models/validate-and-add + provider-nodes/validate SSRF guard.
