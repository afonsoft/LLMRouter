# SPEC-013 — Landing, Docs, Error pages, PWA, Onboarding

## Goal

All non-dashboard surface: `/` landing, `/docs`, error pages, PWA, onboarding wizard.

## Upstream sources

- `src/app/{landing,docs,login,forgot-password,offline,maintenance,status,onboarding?}/**`,
  `src/app/{400,401,403,408,429,500,502,503}/**`, `src/app/(dashboard)/dashboard/onboarding/**`
- `public/{sw.js,favicon*,icon-*,manifest*}`, `src/shared/components/PwaRegister.tsx`
- 9router `src/app/landing/**` (simpler landing)

## Scope

- Landing page (`/`): port OmniRoute landing (hero, features grid, provider marquee,
  CTA → /dashboard or /login) with the same sections, animations kept simple via CSS.
- `/docs`: embedded docs viewer (markdown from `docs/` repo dir rendered like their
  fumadocs theme — reuse the token CSS; sidebar nav of doc pages).
- Error pages 400/401/403/408/429/500/502/503 + `ErrorPageScaffold` port (icon, code,
  message, back-home CTA) wired to ASP.NET status-code pages + Blazor error routes.
- PWA: `manifest.webmanifest`, `sw.js` port (static-asset cache), install prompt
  component, `apple-touch-icon`, offline page.
- Onboarding: first-run wizard (`/dashboard/onboarding`): welcome → set admin
  password → add first provider → create first API key → done flag in settings
  (`setupComplete`).
- `/dashboard/changelog` (renders changelog.d/ + CHANGELOG.md), `/status` public
  status page, `/dashboard/profile`, `/dashboard/api-endpoints` (endpoint catalog
  page listing every exposed route — self-documenting from endpoint metadata).

## Tests

- Status-code pages render correct scaffolds; onboarding flag flow; manifest valid.
