# SPEC-057 — Audit profundo

Upstream: `audit/{a2a,mcp}` pages, `mcp/audit(+stats)`, `compliance/audit-log`.
Our `auditEvents` table captures few event types.

## Scope
- Emit audit events for: auth (login fail/success), keys (create/reveal/
  regenerate/delete), connections CRUD, settings writes, cli import,
  relay tokens, jobs run-now, purge ops — `{actor,action,target,meta,ip,at}`.
- `mcpToolCalls` audit table for every MCP proxy call
  (server,tool,args-hash,duration,ok) + `GET /api/mcp/audit` + `/stats`.
- `GET /api/compliance/audit-log` — filtered export (actor/action/range) in
  json/csv for compliance pulls.
- UI: `/dashboard/audit` rewritten with tabs events/mcp/compliance +
  `/dashboard/audit/a2a` view of a2a task audit (joins SPEC-053 history).
- Tests: login failure audited; key reveal audited; mcp call row written;
  compliance export filters by action.
