# SPEC-041 — Advanced keys + quota

Upstream: `keys/groups*`, `keys/{id}/{access,devices,reveal,regenerate,
usage-limits}`; `quota/{groups,plans,pools/{usage,log,schedules},preview,
keys/[id]/models}`. Today `/keys` is flat CRUD and `/quota` is simple.

## Scope
- Keys: `keyGroups` table {id,name,keys[]}; `GET/POST /api/keys/groups`;
  `POST /api/keys/{id}/regenerate` (new key, same settings);
  `POST /api/keys/{id}/reveal` (returns full key, audit-logged);
  `GET /api/keys/{id}/usage-limits` + `PUT` (per-key rpm/daily-token caps —
  enforced via SPEC-039 rules auto-created).
- Quota: `quotaPlans` table {id,name,limits[],price?}; assign plan→key;
  `POST /api/quota/preview` simulates a request against plan+pools;
  `GET /api/quota/pools/{id}/usage` and `/log`; `quotaSchedules` for
  windowed resets (executed by SPEC-038 jobs).
- UI: extend `/dashboard/keys` (groups section + actions column) and
  `/dashboard/quota` (plans tab + preview panel).
- Tests: regenerate keeps limits; usage-limit blocks after cap; preview
  returns would-allow/deny with reason.

## Non-goals
- Billing/payment plumbing (plans are informational limits only).
