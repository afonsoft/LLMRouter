# SPEC-114 — API Keys page parity (`/dashboard/api-manager`)

Upstream: card grid + key-routing editor + rich filters. Ours: single table.

## Work

- "API key routing" section at top: full-width entry point to the model/
  reasoning routing rules editor ("Configure routing" button → existing
  routing rules UI).
- Registered keys as **cards** (provider icon, name, masked key, permission
  chips, usage count, created date, per-card actions Edit/Regenerate/Disable/
  Delete) — replace the plain table.
- Filter chips row with counts: All / Active / Disabled / Bound / Expired /
  Free / Standard / Managed / Provisioned + search box + "Active only" toggle.
  Client-side filter on the loaded key list.
- "+ Create API key" button stays top-right; keep `/api/keys` API unchanged.
- Key groups section preserved below.

## Tests (≥4)

- Chips filter the rendered card set (e.g. Disabled shows only disabled).
- "Active only" toggle hides disabled keys.
- Search matches name/key prefix.
- Card renders masked key + permission chips; create flow still works.
