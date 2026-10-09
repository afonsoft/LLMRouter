# SPEC-055 — Radar completo

Upstream: `radar/{catalog,intel(+sync),offers(+sync),referrals,settings,
status,sync,sync-all}` + pages `radar/{combos,intel,offers,setup}`. Our
`/radar` is a local-only scan page.

## Scope
- `radarCatalog`/`radarOffers`/`radarIntel`/`radarReferrals` tables + sync
  jobs (SPEC-038) pulling the upstream radar endpoints' public feeds
  (model catalogs, free-tier offers, referral links) — sources configurable
  in settings.
- Endpoints: `GET /api/radar/{catalog,offers,intel,referrals,status}`,
  `POST /api/radar/sync` + `/sync-all`, `GET/PUT /api/radar/settings`.
- `radar/combos` — suggested combos generated from catalog (free models +
  known-good chains) → one-click create combo.
- UI: `/dashboard/radar` rewritten: tabs combos/intel/offers/setup +
  sync status + per-item "add as connection/combo".
- Tests: seed catalog rows → combos suggestion endpoint returns chain;
  sync handles feed failure gracefully.
