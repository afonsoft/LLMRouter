# SPEC-096 — Missing pages + settings/misc endpoints

Gap §3.3 + leftover settings endpoints of `docs/analysis/paridade-2026-10-10.md`.

## Legal/error pages (upstream has all of them)

- `/privacy` + `/terms` — static legal pages (reusable prose, styled like
  landing; marked `noindex`).
- `/offline` — PWA offline fallback (already the PWA target — render real page).
- `/forbidden` — 403 page (StatusCodePages re-execute target).
- `/maintenance` — maintenance-mode page; `settings.maintenanceMode` flag
  redirects non-API traffic here when enabled.
- `/forgot-password` — reset-request page: posts to
  `POST /api/auth/forgot-password` which stores a reset token row + logs it
  (no SMTP in docker yet — the endpoint returns `{sent:true}` and surfaces
  the token in server logs; `POST /api/auth/reset-password` consumes it).
- `/authorize` + `/auth/callback` — minimal OAuth-style authorize/callback
  pages for future OIDC (renders the allow/deny prompt; backend may 501
  until OIDC lands — page + route still ships).
- `/miniapp` — minimal embedded app shell (telegram/miniapp entry; reuse
  dashboard chrome minus sidebar).

## Dashboard gaps

- `/dashboard/providers/new` — dedicated add-provider page (the list page's
  inline dialog stays; this is the deep-linkable version).
- `/dashboard/plugins/{id}/config` — plugin config editor page.
- `/dashboard/media-providers/{id}` — verify `MediaProviderDetail` route
  matches upstream `/dashboard/media-providers/{p}` (fix @page if needed).
- `/dashboard/cli-agents/{id}` + `/dashboard/cli-code/{id}` — detail pages for
  cli-tools/cli-code rows (read-only detail + config editor).
- `/dashboard/radar/{combos,intel,offers,setup}` — radar subpages (data feeds
  exist after SPEC-095 radar detail endpoints; render lists + actions).
- `/dashboard/compression` + `/dashboard/compression/live` — compression
  studio companion pages (rules list + live preview console over
  `/api/compression/*` endpoints).
- `/dashboard/context` — index page for the context section (cards linking to
  combos/assignments/rtk/analytics).
- `/dashboard/logs/activity` — activity view joining auditEvents + usage
  (the `/dashboard/activity` page exists; this route mirrors it for parity).
- `/dashboard/api-manager/{id}/routing` — verify route matches upstream
  `/dashboard/api-manager/routing` deep link (add alias @page if needed).
- `/docs/api-explorer` — alias of the openapi explorer page for parity.
- `/dashboard/providers/services/{p}/embed/{*}` — embed view rendering a
  service's own UI inside the dashboard (iframe + auth handoff; services
  detail endpoints exist).

## Settings endpoints (the useful remainder)

`GET/PUT /api/settings` covers the main blob. Add the upstream sub-endpoints
that aren't covered by it and matter for docker ops:

- `GET/PUT /api/settings/ip-filter` — allow/deny CIDR lists (pairs with
  SPEC-047 ipFilter middleware).
- `GET/PUT /api/settings/feature-flags` — feature-flag map exposed to client.
- `POST /api/settings/purge-{cache,logs,sessions}` — the three purge ops.
- `GET/PUT /api/settings/database` + `POST /api/settings/database/vacuum` —
  DB info + VACUUM op.
- `GET/PUT /api/settings/cache-config` + `GET /api/settings/cache-metrics` —
  cache TTL/size config + metrics.
- `GET/PUT /api/settings/auto-disable-accounts` — auto-disable policy.
- `GET/PUT /api/settings/background-degradation` — degradation thresholds.
- `GET/PUT /api/settings/system-prompt` — global injected system prompt.
- `GET/PUT /api/settings/thinking-budget` — thinking-budget caps.
- `GET/PUT /api/settings/tier-config` — tier definitions.
- `GET/PUT /api/settings/reasoning-routing-rules` — reasoning routing rules.
- `GET/PUT /api/settings/task-routing` — task-type → combo mappings.
- `POST /api/settings/favicon` — favicon upload (stores in kv assets).
- `GET /api/settings/authz-inventory` — inventory of authed routes+scopes.
- `GET /api/settings/lkgp-cache` + `POST /api/settings/lkgp-cache/clear` —
  last-known-good-provider cache view/clear.
- `GET/PUT /api/settings/qdrant/{collections,limits}` — qdrant detail ops
  (vector memory settings).
- `GET /api/settings/cc-discovery-metrics` — CC discovery metrics view.
- Skip: proxies egress ops, notion/obsidian/webdav, local-corpus, mitm,
  free-proxies, models-dev, payload-rules, quota-store/state, deploys —
  desktop/proxy scope, stay on backlog.

## Misc leftovers

- `ANY /api/upstream-proxy/{*path}` — generic upstream passthrough for
  debugging (loopback-admin only).
- `ANY /api/cursor-cli/{*path}` — cursor-cli sub-API catch-all.
- `POST /api/codex/connect/{id}` — codex connection test+attach.
- `GET /api/cli/tokens` — list CLI device tokens (approve/revoke exist).
- `GET /api/internal/codex-responses-ws` — internal WS diagnostic (401-safe
  info stub).
- `POST /api/version-manager/{start,stop}` — start/stop the version-manager
  job (check/install/status exist).
- `ANY /api/vnc-session/{*path}` — VNC session proxy catch-all (extends the
  existing start/status/stop).
- `ANY /api/middleware/hooks` + `/{path}` — registered middleware hook list +
  detail.

## Tests (≥8)

- each new page renders (Razor build + headless sweep: no zombie/404 on route);
- forgot-password issues + consumes a reset token (password changes);
- maintenanceMode redirects UI to /maintenance but leaves /api free;
- settings ip-filter round-trip + enforcement;
- purge ops clear their targets; database/vacuum runs;
- system-prompt/thinking-budget/tier-config round-trip;
- authz-inventory returns route list;
- cli/tokens lists seeded tokens; vnc-session catch-all proxies;
- plugins/{id}/config page loads the plugin config.
