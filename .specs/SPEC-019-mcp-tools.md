# SPEC-019 — MCP canonical tools

## Goal
Expand /mcp beyond `chat__*` to the upstream canonical tool surface.

## Upstream sources
- `open-sse/mcp-server/**` (45 canonical tools, scopes)

## Scope
- tools/list exposes: providers.list/get/test, models.list, connections.list/create,
  apiKeys.list/create/revoke, combos.list/create/run, usage.stats/timeseries,
  logs.list, settings.get/update, skills.list, memory.search/add, pools.list,
  token-health.list, docs.search, system.status, chat (combo call).
- Keep `chat__<model>` dynamic tools.
- Scope enforcement minimal (read vs write implied by tool).

## Tests
- tools/list returns canonical names; tools/call dispatches a few (providers.list,
  usage.stats, combos.list).
