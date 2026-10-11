# SPEC-115 — Providers page parity (`/dashboard/providers`)

Upstream groups providers by auth type with status chips and expandable
rows; our tile grid is close but has raw enum tab labels and overflowing
logos.

## Work

- Category tab labels → friendly text: `API Key`, `Audio`, `Cloud Agent`,
  `Local`, `No-Auth`, `OAuth` (map from APIKEY_PROVIDERS etc.); keep counts.
- Filter chips: All / Connected / Not connected / Error (+ counts from
  `providerConnections`), search, "+ Custom endpoint", "Import from file",
  "Show All".
- Sections "API Key Compatible Providers" and "OAuth Providers": rows (or
  compact tiles) with connected-account badges and expandable detail
  (connection list, per-connection actions).
- Fix logo sizing: constrain provider icon inside the tile (max-height,
  object-fit contain) — Dahl/OpenCode Zen currently overflow.
- Keep Bulk import.

## Tests (≥4)

- Tab labels render friendly names (no `*_PROVIDERS` strings).
- Connected chip filters to providers with ≥1 active connection.
- Expanding a provider row lists its connections.
- Logo renders within tile bounds (class/style check).
