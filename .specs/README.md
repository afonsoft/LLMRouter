# LLMRouter Migration Specs

Migration of **OmniRoute** (`diegosouzapw/OmniRoute`, Next.js/React/TS) and **9router**
(`decolua/9router`) to a single **C# .NET 10 + Blazor WebAssembly** app.

## Pinned upstream sources

Fetch these exact trees when executing a spec — do not migrate a moving target:

| Repo | Commit | Date |
|------|--------|------|
| `github.com/diegosouzapw/OmniRoute` | `61e07fb7e0d4e1e76111495d3718c9e4d06d2a62` | 2026-10-08 |
| `github.com/decolua/9router` | `ce4460ef79382bfddb4aa5fc0ff9f3cb0d5f95a8` | 2026-10-08 |

```bash
git clone https://github.com/diegosouzapw/OmniRoute /tmp/omniroute && git -C /tmp/omniroute checkout 61e07fb7e0d4e1e76111495d3718c9e4d06d2a62
git clone https://github.com/decolua/9router /tmp/9router && git -C /tmp/9router checkout ce4460ef79382bfddb4aa5fc0ff9f3cb0d5f95a8
```

OmniRoute is a superset fork of the 9router lineage (same `open-sse/`, `mitm/`, `sse/`,
`src/shared/` skeleton). The visual identity (OpenClaw × ClawHub palette, SF Pro stack,
Material Symbols, graph-paper wallpaper) and the nav structure come from OmniRoute;
9router contributes the simpler canonical behavior for shared features and its own
extras (pxpipe, basic-chat, token-saver, mitm page).

## Rules for every spec

1. **One branch + one PR per spec**: `devin/spec-NNN-<slug>` → base `main`.
2. **Layout fidelity**: port JSX→Razor keeping Tailwind classes verbatim; keep the
   CSS custom properties in `src/LLMRouter.Client/wwwroot/css/app.css` as the single
   source of truth (they mirror OmniRoute `src/app/globals.css` byte-for-byte where
   possible). Icons = Material Symbols Outlined names. Fonts = `--font-sans`/`--font-mono`.
3. **i18n**: every user-facing string goes through the `L.T()` localization service with
   keys matching OmniRoute `src/i18n/messages/en.json` namespaces; add missing keys to
   `en.json` + `pt-BR.json` + `es.json` under `wwwroot/locales/`.
4. **Data fidelity**: SQLite tables mirror upstream `src/lib/db/schema.js` exactly
   (same table/column names); JSON blobs stay JSON in `Data` columns.
5. **API fidelity**: public gateway paths `/v1/*` and `/api/v1/*` (same handler mounted
   twice); management API under `/api/*` mirrors upstream route names and JSON shapes.
6. **Tests**: xUnit + Shouldly. Every spec ships unit tests for its domain logic and at
   least one endpoint test per new API group (WebApplicationFactory). Run the whole
   suite before pushing.
7. **Docs**: update `docs/analysis/feature-matrix.md` status column when a spec lands.

## Roadmap

| Spec | Title | Status |
|------|-------|--------|
| SPEC-001 | Foundation: solution, design system, shell, auth, core gateway, core pages | in progress |
| SPEC-002 | Provider connections management (all auth types, nodes, test) | pending |
| SPEC-003 | Format translators complete + remaining gateway endpoints | pending |
| SPEC-004 | Combos advanced (round-robin, sticky, vision adapter, combo studio) | pending |
| SPEC-005 | Usage, analytics & costs (charts, daily rollup, pricing) | pending |
| SPEC-006 | Logs, monitoring & resilience (request logger, console, health) | pending |
| SPEC-007 | Settings pages (all sections incl. feature flags, sidebar prefs) | pending |
| SPEC-008 | Quota tracker, token saver, proxy pools | pending |
| SPEC-009 | CLI tools, translator UI, skills pages | pending |
| SPEC-010 | Media providers (tts/stt/image/video/embedding/search) | pending |
| SPEC-011 | OAuth flows + provider detail pages | pending |
| SPEC-012 | MCP, A2A, conductor, orchestration | pending |
| SPEC-013 | Landing, docs, error pages, PWA | pending |
| SPEC-014 | MITM relay + traffic inspector | pending |
| SPEC-015 | Gamification, radar, discovery, free-tiers, leaderboard | pending |
| SPEC-016 | CLI binary, Docker, distribution | pending |
