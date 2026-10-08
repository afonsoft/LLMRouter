# SPEC-007 — Settings pages (all sections)

## Goal

Full `/dashboard/settings/*` parity — 12 sidebar items.

## Upstream sources

- `src/app/(dashboard)/dashboard/settings/**`
- `src/app/api/settings/**`, `src/lib/{config,build-profile,toolPolicy*,source*}`
- 9router `src/app/(dashboard)/dashboard/settings/**` (simpler canonical)

## Scope (one subsection each)

- `/settings/general` — base URL display, admin password change, port/host env notes, language, setup rerun.
- `/settings/appearance` — theme (light/dark/system), accent, density; sidebar prefs shortcut.
- `/settings/ai` — default model/combo, temperature, thinking defaults, auto-title.
- `/settings/modality-bridge` — imageToText bridging config (which model describes images for non-vision models).
- `/settings/routing` — global routing: default strategy, model aliases editor, capability auto-switch toggle, emergency fallback provider.
- `/settings/resilience` — retry counts per status (429/5xx), cooldown durations, circuit-breaker thresholds.
- `/settings/advanced` — log retention, request-details capture toggle, DB path, export/import settings JSON, dangerous zone (reset).
- `/settings/security` — API-key scoping rules, CORS origins, dashboard auth requirement, rate limiting.
- `/settings/access-tokens` — extra dashboard tokens (separate from gateway apiKeys) CRUD.
- `/settings/feature-flags` — flag list with descriptions (port `featureFlag*Description` i18n keys; store flags in `settings.data.featureFlags`).
- `/settings/cache` — response/semantic cache settings (`src/lib/cache`, `semanticCache`, `promptCache`).
- `/settings/sidebar` — reorder sections/items (drag), hide sections/groups/items, pin defaults — drives `SidebarModel` filtering via the same localStorage keys upstream uses (`sidebar-*`, plus server `settings.data.sidebar`).

All persist to `settings.data` JSON via `GET/PUT /api/settings` subkeys, matching upstream key names.

## Tests

- Settings round-trip per section; feature-flag gating affects nav visibility; sidebar prefs persist.
