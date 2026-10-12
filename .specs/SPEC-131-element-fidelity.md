# SPEC-131 — Element-level fidelity vs OmniRoute (computed-style deltas)

## Goal

Close the remaining element-level deltas found by the computed-style audit
(`element-audit.mjs`: computed styles of inputs/selects/buttons/cards/typography
on the 16 top pages, LLMRouter vs omniroute.afonsoft.dev).

## Deltas found (omni → llmr)

| Element | OmniRoute | LLMRouter (before) |
|---|---|---|
| `.input` generic | `py-2 px-3` (8/12), `text-sm` 14px, `bg-white`, `border-black/10`, `rounded-control` 9px, `shadow-inner` | 8/12, 13px, surface, no inner shadow |
| Sidebar search input | white bg, h34, `pl-10` (40px icon space), 14px, shadow-inner | `surface-2` fill, h31, pl32, 13px |
| `.btn` md | `h-9 px-4 text-sm` = h36/px16/14px, `rounded-control`, `shadow-sm` | ~h33, px14, 13px |
| Page header title | `text-xl font-semibold tracking-tight` = 20px/600 | `.page-title` 22px/700 |
| `/docs` | standalone **dark** portal (`fumadocs DocsLayout`, `defaultTheme:"dark"`, own top nav: title + Home + Dashboard + GitHub + LanguageSelector, `--fd-sidebar-width:220px`) | renders inside light dashboard shell — inherits `--fg` light tokens (bug) |
| `.table th` | uppercase muted | already aligned |

## Scope

1. `.input`, `select.input`, `textarea.input`: font-size 14px + `box-shadow: inset 0 2px 4px rgba(0,0,0,.05)` (tailwind `shadow-inner`).
2. `.sidebar-search input`: white `--surface` bg, height ~34px, `padding: 7px 12px 7px 40px`, font 14px, same inner shadow; icon position adjusted.
3. `.btn`: height 36px, `padding: 0 16px`, font 14px; `.btn-sm` keeps compact sizing (h28/px12/12px).
4. `.page-title`: 20px/600 tracking-tight.
5. `/docs`: new `Layout/DocsLayout.razor` — standalone dark portal (bg `#0b0e14`, surface `#161b22`, text `#e6e6ef`, muted `#a1a1aa`, border `rgba(255,255,255,.08)`, primary `#e54d5e`), top nav with app mark + "Docs" title, links Home / Dashboard, GitHub link, language selector; tree column 220px. `DocsPage` uses `@layout Layout.DocsLayout` and scopes its tokens under the portal wrapper.
6. e2e: extend `smoke.mjs` with computed-style assertions for `.input`/`.btn`/`.page-title` and a `/docs` dark-bg check so deltas can't regress.

## Out of scope

- Structural page work: `/dashboard/health` missing upstream sections (concurrency queues, provider health matrix, telemetry cards) → SPEC-132. `/dashboard/cli-agents` upstream select+list structure → SPEC-133.
