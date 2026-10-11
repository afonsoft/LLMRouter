# SPEC-124 — Home page finishing parity (`/home`)

Both apps already have welcome header + stats + recent requests. Remaining
upstream elements to port (skip sponsor banners — they are OmniRoute
partner ads, not generic features):

## Work

- Quick Start as numbered-step card matching upstream (1 Create API key →
  2 Connect providers → 3 Point your client → 4 Monitor & optimize),
  each step linking to its page; "Full docs" link.
- "Provider Topology" card (providers by type/status mini-graph or list)
  like upstream's home widget.
- Recent requests table: keep, but match upstream column order
  (Model/Provider/Status/When) and row density.
- Dismissible announcement/banner slot driven by `settings.announcement`
  (generic, replaces upstream's sponsor slots).

## Tests (≥3)

- Quick Start shows 4 steps with links.
- Topology card lists connected providers.
- Announcement renders when settings.announcement set; hides when empty.
