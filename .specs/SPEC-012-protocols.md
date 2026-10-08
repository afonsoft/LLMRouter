# SPEC-012 — MCP, A2A, Conductor, Orchestration

## Goal

Protocol/agent pages: `/dashboard/mcp`, `/dashboard/a2a`, `/dashboard/acp-agents`,
`/dashboard/conductor`, `/dashboard/orchestration`, `/dashboard/agent-bridge`,
`/dashboard/cloud-agents`, `/dashboard/playground` upgrades.

## Upstream sources

- `src/app/(dashboard)/dashboard/{mcp,a2a,acp-agents,conductor,orchestration,tools/agent-bridge,cloud-agents,playground}/**`
- `src/lib/{mcp?,a2a/,acp/,conductor/,cloudAgent/,playground/}` , `src/app/api/{mcp,a2a,acp,conductor,orchestration,playground,cloud}/**`
- `open-sse/` ACP handlers; `src/lib/ws/` (websocket layer)

## Scope

- MCP: server registry (stdio/sse/http), tool listing per server, enable/disable,
  expose gateway models over MCP (`/mcp` JSON-RPC endpoint) so agent CLIs can mount
  LLMRouter as an MCP server — mirror upstream endpoint shapes.
- A2A: agent cards (`/.well-known/agent.json` style), task endpoint
  (`/a2a` JSON-RPC), agents list page.
- Conductor/orchestration: multi-step routing (chain/combo workflows with steps,
  variables, fan-out/fan-in) — read upstream conductor lib for the execution model.
- Agent bridge + cloud agents: bridge local agent CLIs to the router (agent-bridge
  API), cloud agent session tracking page.
- Playground v2: multi-model side-by-side chat, params panel (temperature, max tokens,
  thinking effort), token/cost per message, save/load conversations
  (`conversations` table + `/api/conversations`).

## Tests

- MCP JSON-RPC initialize/tools/list round-trip; A2A agent card + task flow; conductor
  step engine with fake models.
