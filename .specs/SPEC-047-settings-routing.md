# SPEC-047 — Settings routing rules

Upstream: `settings/{ip-filter,free-proxies*,oneproxy(+rotate),
payload-rules,reasoning-routing-rules(+simulate),task-routing}`.

## Scope
- `ipFilter` — allow/deny CIDR lists checked in middleware before auth;
  `GET/PUT /api/settings/ip-filter`.
- `payloadRules` — ordered {match(provider|model|regex), action(set|remove|
  redact|cap), field, value} applied to request body pre-dispatch in
  GatewayPipeline; CRUD endpoints.
- `reasoningRoutingRules` — {pattern, effort(budget|max|off), provider?}
  mapping requests to reasoning models; `POST .../simulate` returns matched
  rule + resolved effort without dispatch.
- `taskRouting` — {taskType→model/combo} table used by agents endpoints.
- `freeProxies` — fetch+score free proxy lists into proxy-pools (reuse
  SPEC-027 health checks), auto-rotate.
- `oneproxy` — single upstream HTTP proxy for all provider calls, with
  rotate-on-fail.
- UI: sections under `/dashboard/settings` (or a Routing page): ip-filter
  lists, payload-rule builder, reasoning simulate panel, task-routing table,
  free-proxy toggle+count.
- Tests: ip-filter blocks a CIDR; payload rule redacts a field; reasoning
  simulate returns expected effort; free-proxy list populates pool.

## Non-goals
- Residential/paid proxy integrations.
