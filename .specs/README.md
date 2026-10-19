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
8. **Spec file in the same PR**: the `.specs/SPEC-NNN-*.md` file must be committed in
   the same PR as the implementation — no code-first/doc-later. (Added after the
   SPEC-145 audit found SPEC-126–130/140 landed without spec files.)

## Roadmap

**Status (2026-10-18, SPEC-145 audit): every spec below is merged to `main`
(PRs up to #184).** Per-spec PRs in git history (`git log --grep 'SPEC-'`).

| Spec | Title | Status |
|------|-------|--------|
| SPEC-001 | Foundation: solution, design system, shell, auth, core gateway, core pages | done |
| SPEC-002 | Provider connections management (all auth types, nodes, test) | done |
| SPEC-003 | Format translators complete + remaining gateway endpoints | done |
| SPEC-004 | Combos advanced (round-robin, sticky, vision adapter, combo studio) | done |
| SPEC-005 | Usage, analytics & costs (charts, daily rollup, pricing) | done |
| SPEC-006 | Logs, monitoring & resilience (request logger, console, health) | done |
| SPEC-007 | Settings pages (all sections incl. feature flags, sidebar prefs) | done |
| SPEC-008 | Quota tracker, token saver, proxy pools | done |
| SPEC-009 | CLI tools, translator UI, skills pages | done |
| SPEC-010 | Media providers (tts/stt/image/video/embedding/search) | done |
| SPEC-011 | OAuth flows + provider detail pages | done |
| SPEC-012 | MCP, A2A, conductor, orchestration | done |
| SPEC-013 | Landing, docs, error pages, PWA | done |
| SPEC-014 | MITM relay + traffic inspector | done |
| SPEC-015 | Gamification, radar, discovery, free-tiers, leaderboard | done |
| SPEC-016 | CLI binary, Docker, distribution | done |

### Landed spec waves (017+)

Titles live in each spec file's H1 — this index tracks coverage, not content:

| Range | Theme | Status |
|-------|-------|--------|
| SPEC-017–030 | Parity wave 1 (batches, translators depth, MCP tools/transports, combo strategies, resilience, CI/CD, guardrails, UX, memory, MITM, responses/free-tiers, PWA+E2E) | done |
| SPEC-031–067 | Parity wave 2 (plugins, localCorpus, memory backends, context engines, usage/analytics, backups, jobs, rate-limit, files API, keys/quota, log export, playground, openapi, prompt cache, settings ops/routing, relay, session pools, device login, vscode tokens, evals, a2a/conductor, provider ops, radar, gamification, audit, skills extras, version manager, tunnels, sidecar, qdrant, media gateway, /v1 extras, bots, admin, remaining pages) | done |
| SPEC-068–079 | Ops/registry depth (provider ops, combo ops/health, resilience core, model registry, auto-combos, core misc, throughput, protocol parity, custom headers, auto-router, sonar sweep) | done |
| SPEC-081–100 | Upstream-delta waves + gap closure (small knobs, combo steps 2.0, mappings, routing brain, discovery, deltas v3.8.52 ×3, scoped providers, /v1 leftovers, usage-logs, combo analytics, mgmt batch, MCP tools, medium features, pages, kv partition, llmlingua, MITM policy) | done |
| SPEC-101–110 | Market comparison (see table below) | done |
| SPEC-111–125 | Virtual keys, public catalog/signup, UI-parity pages 113–124, oauth CLI cred import | done |
| SPEC-126–130 | Feature-coverage queue (cli-tools depth, agent-bridge + traffic inspector, provider passthrough, a2a/gami depth, provider auth) | done — **specs retroativos** (docs escritos na SPEC-145) |
| SPEC-131–144 | Element/UI fidelity + OpenRouter-style account (element fidelity, health sections, cli agents, users admin, signup/billing, stripe, MP/PagSeguro, account keys/activity, combo pricing, generation detail, plan tiers) | done — SPEC-140 retroativo |
| SPEC-145 | Specs/docs reconciliation (esta reconciliação) | in progress |

**SPEC-080 nunca foi usado** (número pulado — sem arquivo nem PR).
SPEC-126–130 e SPEC-140 são specs retroativos — a regra 8 previne recorrência.

## Market-comparison specs (SPEC-101+)

SPEC-001..100 closed the upstream parity roadmap (OmniRoute/9router). SPEC-101+
come from `docs/analysis/comparativo-mercado-2026-10-11.md` (LLMRouter vs market
gateways) — improvement work, not ports. All done:

| Spec | Title | Origin (doc §8) |
|------|-------|-----------------|
| SPEC-101 | OTEL + `/metrics` Prometheus export | quick win 1 |
| SPEC-102 | Request-level routing hints (OpenRouter `provider` object) | quick win 2 |
| SPEC-103 | Audit/telemetry retention TTL + purge job | quick win 3 |
| SPEC-104 | Config-as-code export/import/diff | quick win 4 |
| SPEC-105 | Public benchmark methodology | quick win 5 |
| SPEC-106 | Semantic cache (opt-in) | medium 6 |
| SPEC-107 | Response guardrails + PII redaction | medium 7 |
| SPEC-108 | Async usage sink | medium 8 |
| SPEC-109 | EF Core migrations | medium 9 |
| SPEC-110 | Tamper-evident audit hash-chain | structural 12 |
| SPEC-111 | Virtual keys + credit ledger + per-user rate-limit | multiuser fase 1/2 |
| SPEC-112 | Public model catalog + signup surface | multiuser fase 2 |

Multi-user/RBAC/OIDC + Redis multi-node + OpenRouter-style API resale are
documented as future evolution in
`docs/analysis/evolucao-multiuser-openrouter.md` (SPEC-111/112 landed its
foundation phases; OIDC/RBAC remain future work).

UI-parity migration specs (OmniRoute → LLMRouter), ordered by impact —
audit: `docs/analysis/ui-parity-audit-2026-10-11.md` — all done:

| Spec | Title |
|------|-------|
| SPEC-113 | Activity feed parity |
| SPEC-114 | API Keys page parity |
| SPEC-115 | Providers page parity |
| SPEC-116 | Logs pages parity (request/console/proxy) |
| SPEC-117 | Analytics pages parity (utilization/combo-health) |
| SPEC-118 | Costs pages parity (overview/pricing/budget/quota-share) |
| SPEC-119 | Combos + Model catalog parity |
| SPEC-120 | Health + Leaderboard parity |
| SPEC-121 | Profile/CLI pages parity (+content fixes) |
| SPEC-122 | Docs hub parity |
| SPEC-123 | Settings sub-routes parity (optional) |
| SPEC-124 | Home finishing parity |
| SPEC-125 | OAuth / CLI credential import |
| SPEC-146–157 | Antirion gap-scan wave (alerts engine, safety events + guardrails pack, request log pro, key scoping, budget overage, UX transversal, routing pack, change control, device auth, test-mode, log sinks, release hardening) | in progress |
