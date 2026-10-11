# SPEC-113 — Activity feed parity (OmniRoute `/dashboard/activity`)

Origin: `docs/analysis/ui-parity-audit-2026-10-11.md`. Upstream screen =
categorized event feed; ours = plain table.

## Work

- Category filter chips across the top: All, Providers, Combos, API Keys,
  Settings, Quota, Auth, System — filtering `auditEvents` by category.
- Group events by day with date headers ("Today", "2025-10-04", …).
- Relative timestamps ("just now", "6 days ago") with absolute on hover.
- Per-category icon chip before each row (dns, view_list, vpn_key, settings,
  percentage, lock, memory…), matching upstream mapping.
- "Refresh" button top-right; keep auto-prepend of new events.
- Server: `/api/audit/events` already exists — extend with `?category=` and
  cursor pagination if missing; categories derived from `eventType` prefix
  (`provider.*`, `combo.*`, `apikey.*`, `settings.*`, `quota.*`, `auth.*`,
  else `system`).

## Tests (≥4)

- Category chip filters rows server-side (`?category=providers`).
- Grouping renders a date header per distinct day.
- Relative time renders ("just now" for fresh row).
- Icon chip present per row; unknown type falls back to generic icon.
