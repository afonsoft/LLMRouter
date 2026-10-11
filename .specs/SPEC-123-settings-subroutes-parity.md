# SPEC-123 — Settings sub-routes parity (optional)

Upstream has dedicated routes `/dashboard/settings/{general,appearance,
ai,modality-bridge,routing,resilience,advanced,security,access-tokens,
feature-flags,cache,sidebar}`. Ours consolidates into one page with tabs
— URLs land on the page but don't deep-link the matching tab.

## Work

- Route each `/dashboard/settings/<tab>` to the Settings page with the
  corresponding tab preselected (router state or query param `?tab=`).
- Keep single-page implementation; only deep-linking is added.
- Optional: sidebar item "Settings" expands to the sub-list like upstream.

## Tests (≥2)

- `/dashboard/settings/security` opens the Security tab.
- Tab switch updates the URL (history replace, no reload).
