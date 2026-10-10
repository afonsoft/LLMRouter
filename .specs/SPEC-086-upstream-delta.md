# SPEC-086 — Upstream delta v3.8.52 (headless + security rail + obsidian fix)

Source: `diegosouzapw/OmniRoute` release/v3.8.52 `7510677f2` → `048d9e2f2` (15 commits).
Port the actionable items; skip release-infra/test-harness churn.

## Headless mode (gateway-only, no dashboard)

Upstream `feat(server)`: `OMNIROUTE_HEADLESS=1` / `serve --headless` — process serves
only the gateway + management APIs, no dashboard assets/pages. For docker deployments
the dashboard is optional dead weight (and a smaller attack surface).

- `LLMROUTER_HEADLESS=1` env (or `Db:Headless` config / `--headless` CLI verb):
  - skip Blazor framework files + static web assets + index fallback;
  - UI routes (`/`, `/dashboard/*`, `/login`, `/error/*`, `/docs`, PWA assets) → 404;
  - `/api/*`, `/v1/*`, `/v1beta/*`, `/api/auth/*` keep working (dashboard login page
    excluded; session-cookie auth still valid for the API);
  - `/status` + `/api/version` report `headless: true`.
- CLI: `llmrouter serve --headless` maps to the same flag.
- Docker: document `LLMROUTER_HEADLESS=1` in Dockerfile/compose comments + README.

## Security rail sweep (local-only routes + public-creds guard)

Upstream `security(rail)`: sweep routes that must be loopback-only + structural
guard against public/well-known credential values.

- Audit list of endpoints that upstream marks loopback-only (discovery/scan,
  env repair, db-backups import, version-manager install, service/tunnel control):
  add `settings.data.adminLocalOnly` (default on for destructive/probing ops) —
  non-loopback callers get 403 when enabled; localhost + configured trusted nets
  (private ranges behind `forwarded` headers when `trustProxy` is set) exempt.
- Public-creds structural guard: refuse to store provider connections / api keys
  whose secret matches known-public patterns (example/demo keys, the placeholder
  strings upstream ships, `sk-...-public` fixtures) — 400 with clear error.

## Obsidian memory vault fix

Upstream `fix(memory)`: obsidian memory files must stay inside the vault.

- `ObsidianMemoryBackend`: resolve configured vault root once; any note path that
  escapes it (`..`, absolute path injection via item title/id) is clamped/rejected.
- Same guard for file-backed corpus paths if any exist.

## Tests (≥4)

- headless: `/v1/models` 200, `/dashboard` 404, `/` 404, `headless:true` in /status;
- adminLocalOnly: non-loopback probe → 403, loopback → allowed;
- public-creds: storing a connection with a known public key → 400;
- obsidian: `../x` title path stays inside vault root.
