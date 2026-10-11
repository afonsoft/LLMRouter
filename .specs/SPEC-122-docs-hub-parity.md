# SPEC-122 — Docs hub parity (`/docs`)

Upstream: dark themed documentation portal. Ours: flat filename list.

## Work

- Dark theme surface matching upstream docs layout (own theme, not the
  dashboard light theme).
- Left doc tree grouped by category: Home, Getting Started, Guides,
  Configuration, Architecture, Management, Reference, Routing,
  Monitoring, Security, Providers, Comparison, Operations — build the
  tree from `docs/` front-matter `category:` or a static map file.
- Home view: search box (Ctrl+K focuses), guide cards (Quick Start /
  Auto-Combo Guide / Providers Guide), "For Non-Tech Users" and
  "For Tech Users" link columns.
- Doc rendering keeps markdown viewer; breadcrumb + prev/next.
- "Back to Dashboard" link top-left.

## Tests (≥3)

- Tree renders categories; doc opens under correct node.
- Search filters doc titles (Ctrl+K shortcut focuses input).
- Home cards link to their docs.
