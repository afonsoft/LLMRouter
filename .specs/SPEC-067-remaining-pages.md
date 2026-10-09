# SPEC-067 — Remaining placeholder pages

Upstream pages still routing to PlaceholderPage: `auto-combo`, `limits`,
`radar/*` (covered by SPEC-055), `system/{1proxy,mitm-proxy}`,
`api-manager/[id]/{access,routing}`, `combos/{playground,[id]}`,
`media-providers/[kind]/[id]`, `miniapp`, `maintenance`.

## Scope
- `/dashboard/auto-combo` — auto-combo builder: pick strategy + candidate
  sources (SPEC-064 candidates API) → preview members → create combo.
- `/dashboard/limits` — unified limits page: rate limits (SPEC-039) +
  key usage-limits (SPEC-041) + quota pools overview in one view.
- `/dashboard/system/1proxy` — oneproxy config/status (SPEC-047).
- `/dashboard/system/mitm-proxy` — MITM proxy status + cert install guide
  (existing MITM feature surface).
- `/dashboard/api-manager/{id}/access` + `/routing` — per-API-key access
  editor (groups, models, limits) and routing overrides.
- `/dashboard/combos/{id}` — combo detail page: members order, weights,
  strategy, health; `/dashboard/combos/playground` — combo try-out page.
- `/dashboard/media-providers/{kind}/{id}` — per-provider detail: config,
  kind capabilities, test button, recent calls.
- `/dashboard/miniapp` — embedded miniapp shell route (compatibility).
- `/dashboard/maintenance` — maintenance mode toggle + page shown to
  non-admin requests when on.
- Tests: nav sweep — every sidebar link resolves to a non-placeholder page.
