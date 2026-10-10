# SPEC-087 — Parity-plus improvements (MCP tools + kv partition + registry caps)

Beyond-parity items from `docs/analysis/paridade-2026-10-10.md` §D.

## MCP canonical tools — close the ~14-tool gap

Upstream exposes ~45 canonical tools; we ship 31. Add the cheap ones that map onto
existing endpoints/services:

- `jobs.list`, `jobs.run` — list jobs + run-now.
- `evals.list`, `evals.run` — list suites + trigger a run.
- `routing.explain` — dry-run routing explanation (`/api/routing/explain` logic).
- `memory.reindex` — reindex memory store.
- `quota.preview`, `quota.pools` — quota preview + pool usage.
- `keys.usage-limits` — read/set per-key usage limits.
- `discovery.scan` — run provider discovery probe (loopback guard applies).
- `analytics.combo-health` — combo health summary.

## Hot kv blobs → per-item rows

`kv` scopes storing JSON arrays in a single row (`audit`, `memory`, `webhooks`,
`v1batches` counters already partitioned). Under write load the read-modify-write
blob serializes and risks truncation.

- Migrate `audit` → `auditEvents` table (or per-item kv keys `audit:{ts}:{id}`)
  keeping `GET /api/audit` contract; same treatment for `memory` items
  (`memoryItems` table) and `webhooks` deliveries if hot.
- Migration on boot: if blob exists and table empty → split into rows, mark
  `kv._migrated:{scope}`.

## Registry model capabilities mapping

`providers.json` models carry `supportsVision`/`toolCalling`/`supportsReasoning`/
context fields, but `ComboPlanner.GetCapabilitiesForModel` only reads
`vision`/`capabilities` — most models report empty caps (breaks capability
auto-switch + vision routing).

- `RegistryModel`/`GetCapabilitiesForModel`: map the `supports*` fields + registry
  `capabilities` list + `capabilityOverrides` (overrides win) into one caps record
  {vision, tools, reasoning, json, contextLength}.
- Regression test: a `supportsVision:true` registry model resolves vision=true
  without an override row.

## Tests (≥5)

- each new MCP tool callable via tools/call (list + happy path);
- audit blob migrates to rows once, no dup;
- caps mapping: registry `supportsVision` surfaces in planner caps;
- override still wins over registry caps;
- quota.preview tool returns pool usage.
