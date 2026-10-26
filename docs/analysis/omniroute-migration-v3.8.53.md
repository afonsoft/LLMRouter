# OmniRoute → LLMRouter migration review

**Baseline requested:** `cheaper-inference/OmniRoute` v3.8.53  
**Baseline available:** v3.8.52, commit `ad6f33f66bda34eba98645d3bfdabbde294bc843`, released 2026-10-19  
**Status:** v3.8.53 was not present in the upstream tags or releases when this review was run. The
comparison therefore uses the latest published release and keeps the version gap explicit instead of
inventing a v3.8.53 delta.

## Executive summary

LLMRouter is a real .NET/Blazor migration of OmniRoute, not a separate product with a similar
dashboard. The migration already covers the core gateway, provider registry, SQLite data model,
combos, translators, resilience, usage, logs, and most of the dashboard surface.

The remaining work is mostly **depth and release drift**, not a rewrite:

1. finish the approved SPEC-152–157 wave;
2. revalidate the port against the v3.8.52 source tree after every upstream release;
3. add an explicit migration contract for versioned schemas, route/API shapes, and UI screenshots;
4. prioritize the operational surfaces where OmniRoute is visibly stronger: provider health, routing
   decisions, settings/change safety, live request inspection, and release diagnostics.

## Version and source evidence

| Item | Evidence |
|---|---|
| Requested upstream | `https://github.com/cheaper-inference/OmniRoute` |
| Published baseline used | tag `v3.8.52` |
| Exact commit | `ad6f33f66bda34eba98645d3bfdabbde294bc843` |
| Upstream stack | Next.js/React/TypeScript, SQLite, embedded dashboard |
| LLMRouter stack | ASP.NET Core/.NET 10, Blazor WASM, EF Core, SQLite |
| Upstream dashboard routes | 54 `page.tsx` entries under `src/app/(dashboard)/dashboard` |
| LLMRouter dashboard pages | 100 `.razor` pages under `src/LLMRouter.Client/Pages` |
| Upstream API route files | 750 `route.ts` files, including nested compatibility and provider routes |
| LLMRouter endpoint files | 72 endpoint classes |

The upstream README advertises 375 providers, 20 combo strategies, headless mode, quota-aware
routing, live quota telemetry, model discovery, compression, MCP/A2A, guardrails, and extensive
CLI/desktop integrations. LLMRouter already ports the docker/core subset and intentionally excludes
some Electron, MITM, subscription-auth, and desktop-only behavior.

## Feature parity and migration priorities

| Area | OmniRoute v3.8.52 | LLMRouter now | Migration action |
|---|---|---|---|
| Gateway protocols | OpenAI, Claude, Gemini, Responses, media/passthrough | Same protocol families and `/v1` + `/api/v1` mounting | Keep contract tests against upstream fixtures |
| Provider catalog | Large live catalog, free tiers, synced model availability | Embedded registry plus synced models and capability overrides | Add release revalidation and catalog drift report |
| Combo routing | 20 strategies, auto-combo, fusion/pipeline, quota/cache affinity | Most strategies, auto-router, fusion/pipeline, virtual `auto/*` | Finish SPEC-152 hedging, prefix affinity, predicates, mirror |
| Resilience | Provider breaker, account cooldown, model lockout, quota-aware selection | Equivalent core layers already ported | Add route-decision evidence and stress tests |
| Request observability | Analytics, logs, call detail, route decision headers, quota telemetry | Analytics, logs, request detail, activity, health | Finish log sinks and richer attempt/decision detail |
| Governance | Settings groups, guardrails, scoped keys, backup/restore | Guardrails, scoped keys, budgets, audit, backups | Finish change control, device auth, test mode |
| Dashboard | 54 route pages with dark local-first console | 100 Razor pages with bilingual layout and screenshots | Close high-value visual/interaction gaps, not raw page count |
| Desktop/CLI | Electron, CLI setup, provider OAuth helpers | Core CLI and selected integrations | Keep out of docker parity unless explicitly promoted |
| Release operations | SBOM/provenance, release channels, health tooling | Docker/binary release workflow | Finish SPEC-157 and publish migration evidence |

## Screen comparison

The existing LLMRouter screenshots are the current local application baseline:

| Flow | LLMRouter baseline | Upstream v3.8.52 migration target | Main gap |
|---|---|---|---|
| Home | KPI cards, Quick Start, topology, recent requests | Provider/connection health, quota and action-oriented operations | Surface live health and next action without navigating |
| Providers | 371-provider searchable catalog with category tabs | Grouped provider sections, connected counts, per-group Test All, operational badge | Add connection health/test state and batch actions to the same catalog |
| Analytics | Summary cards linking to Combo health, Utilization, Compression, Search, Evals | Dense time-window analytics with token/cost trend and provider breakdown | Add period comparison and route/provider cost drill-down |
| Logs | Recording, I/O logging, filters, saved views, export/copy link | Request list plus rich attempt/cost/route details and live-tail workflows | Make the request row the entry point to attempts, predicates, and route decision |
| Settings | Existing settings surface and backups | Tabbed General/AI/Security/Routing/Resilience/Advanced console | Add revision history, safe writes, diff, rollback and approval state |

### Simulated migration screens

The target screens should be implemented as incremental slices, preserving the current LLMRouter
shell:

```text
Providers
┌─────────────────────────────────────────────────────────────────────────┐
│ Search providers   [Configured only] [Test all] [Bulk import]            │
│ All 371 | Connected 24 | API key 248 | OAuth 27 | Free 153              │
├─────────────────────────────────────────────────────────────────────────┤
│ Provider       Connection health     Quota/reset       Actions           │
│ Anthropic      ● 2 connected         74% / 42m         Test · Configure  │
│ OpenAI         ● 1 connected         91% / 58m         Test · Configure  │
│ Gemini         ● degraded            cooldown 5m       Re-enable         │
└─────────────────────────────────────────────────────────────────────────┘
```

```text
Request detail
┌─────────────────────────────────────────────────────────────────────────┐
│ 200  842 ms   combo=auto/coding   decision=quota-weighted                │
├─────────────────────────────────────────────────────────────────────────┤
│ Attempts                                                              2  │
│ 1  provider/model-a   408 ms   429  cooldown 60s                       │
│ 2  provider/model-b   434 ms   200  predicate: finish_reason ✓         │
├─────────────────────────────────────────────────────────────────────────┤
│ Cost ledger   input 1.2k · output 640 · estimated $0.0042               │
│ Affinity      hit · prefix 15m TTL       Shadow mirror: match           │
└─────────────────────────────────────────────────────────────────────────┘
```

```text
Settings → Change control
┌─────────────────────────────────────────────────────────────────────────┐
│ Revisions   [area filter] [actor filter] [diff] [rollback]              │
├──────┬───────────────┬──────────────┬──────────────┬─────────────────────┤
│ #184  │ providers     │ Afonso       │ approved     │ View diff           │
│ #183  │ routing       │ automation   │ pending      │ Approve · Deny      │
│ #182  │ security      │ Afonso       │ applied      │ Rollback            │
└──────┴───────────────┴──────────────┴──────────────┴─────────────────────┘
```

These are interaction targets, not claims that the current application already renders every field.
They are intentionally aligned with the current LLMRouter pages and the upstream contracts.

## Recommended execution order

### Already approved and pending

1. **SPEC-152 — Routing pack:** hedge, prefix affinity, response predicates, shadow mirror.
2. **SPEC-153 — Change control:** revisions, approvals, diff, rollback.
3. **SPEC-154 — Device authorization:** CLI device code flow.
4. **SPEC-155 — Test mode:** reset, seed, clock skew, controlled failures.
5. **SPEC-156 — Log sinks:** bounded NDJSON audit/request exporters.
6. **SPEC-157 — Release hardening:** SBOM, provenance, image scan.

### New migration specs

1. **SPEC-158 — Upstream release contract:** pinned commit, release manifest, catalog/schema/API
   drift checks, and a v3.8.53 re-run when the release is published.
2. **SPEC-159 — Provider operations parity:** grouped provider health, batch test, validation,
   quota/expiration indicators, and a UI route that combines connection status with actions.
3. **SPEC-160 — Routing decision observability:** persisted attempt ledger, explainable strategy
   decision, cost/latency/predicate outcomes, and request-detail UI.
4. **SPEC-161 — Dashboard migration fidelity:** screenshot fixtures and Playwright/browser checks
   for Home, Providers, Analytics, Logs, Settings, responsive layout, and EN/PT-BR parity.
5. **SPEC-162 — Compatibility and upgrade validation:** import/export fixtures from OmniRoute SQLite,
   EF migration checks, rollback rehearsal, and release-to-release smoke tests.

## Non-goals

- Do not copy the Next.js implementation into the .NET solution.
- Do not claim v3.8.53 behavior until the tag/release exists and is pinned.
- Do not expand the docker target into Electron/MITM/desktop parity without a separate product decision.
- Do not replace existing LLMRouter features that are beyond the upstream core (billing, virtual keys,
  payments, i18n, gamification) merely to match the upstream menu.

## Verification required for each migration PR

- xUnit tests for the new domain behavior and at least one endpoint/UI contract test.
- `dotnet build` and focused `dotnet test` before push.
- `git diff --check`.
- Screenshot or browser evidence for dashboard work.
- CI green before merge, following the existing one-branch/one-PR-per-spec workflow.
