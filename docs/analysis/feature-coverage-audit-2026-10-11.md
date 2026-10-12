# Feature-coverage audit — LLMRouter vs OmniRoute

**Date:** 2026-10-11
**Method:** Extracted every `route.ts` path under `src/app/api/` in OmniRoute (739 files) and every `Map*` route literal in `src/LLMRouter.Server/Endpoints/` + `Program.cs` (572), normalized `{param}`/`[param]` and `/api`/`/v1` prefixes, then diffed + manually inspected the top-bucket areas. Complements `ui-parity-audit-2026-10-11.md` (visual) — this one is API/feature surface.

Electron desktop app: **out of scope per user decision** (2026-10-11).

## Result at a glance

- Route surface: **~572 mapped endpoints** vs 739 upstream. Most of the numeric delta is upstream's per-key granular management routes vs our consolidated endpoints — cosmetic.
- **Real gaps** (features with zero or thin coverage) are concentrated in ~10 areas listed below.

## Zero coverage (real gaps)

| Area | Upstream routes | What it does |
|------|-----------------|--------------|
| `/api/tools/agent-bridge/*` | ~20 | MITM "agent bridge" — local TLS-intercepting proxy so CLI agents route through OmniRoute: cert generation/download/regenerate, tproxy mode, upstream CA, per-agent detect/detected-models/mappings/dns/reset, bypass, diagnose, repair |
| `/api/tools/traffic-inspector/*` | ~16 | Request traffic inspector — capture modes (http-proxy, system-proxy, tls-intercept), sessions, per-request detail/annotation/replay, HAR export, ws feed |
| `/api/cloud/*` | ~4 | OmniRoute cloud sync — auth, credentials update, model resolve/alias |
| `/api/omniglyph`, `/api/alpha/*` | ~4 | Experimental/alpha features |

## Thin coverage (partial — have the area, missing depth)

| Area | Ours | Upstream | Missing |
|------|------|----------|---------|
| `cli-tools` | ~9 routes | ~45 | Per-tool settings for ~25 CLIs (claude, codex, cline, crush, droid, forge, grok-build, hermes, jcode, kilo, letta, omp, openclaw, pi, qwen, smelt, deepseek-tui, codewhale, antigravity…), apply, backups, logs, all-statuses, keys, detect, runtime/{toolId}, guide-settings |
| `a2a` | 2 (status, tasks) | 6 | tasks/{id}, tasks/{id}/cancel, tasks/history — task lifecycle detail |
| `oauth` | basic OAuth conn flow | +17 | **Local CLI credential auto-import**: codex import/import-token, cursor auto-import/login start+poll+cancel/import, kiro api-key, agy import/import-bulk, cliproxy-import, paste-credentials, oauth/{provider}/{action} |
| `providers` | core mgmt | +~20 | Per-provider passthrough `/providers/{p}/chat/completions` + `/embeddings` + `/images/generations` + `/voices` + `/models` (direct provider-scoped gateway), `{id}/interception-rules`, `{id}/claude-auth|codex-auth|agy-auth apply-local/export`, `{id}/login`, `{id}/refresh*`, `{id}/sync-models`, `{id}/test-message`, `{id}/cc-alias`, `{id}/param-filters`, `{id}/chatgpt-web-codex-doctor` |
| `gamification` | GamiEndpoints (group) | +14 | badges/earned, federation leaderboard+score, invite/redeem, anomalies, level, notifications, rotate, servers, stream, transfer |
| `vscode` | `/v1/vscode/*` proxy surface (~13) | +~10 | Non-v1 `/vscode/{token}/*` variants and `/vscode/raw/*` aliases — mostly redundant URL aliases of what we have |
| `tunnels` | 8 | ~9 | ~parity — check cloudflared/ngrok/tailscale coverage detail |
| `version-manager` | group exists | ~6 | check-update/install/restart/start/status/stop depth |
| `conductor` | 5 files | ~5 | ~parity |
| `discovery` | group exists | ~4 | ~parity |
| `webhooks`, `batches`, `auth/oidc` | present | — | verify subroute depth (oidc callback/login, batches/delete-completed) |

## Cosmetic differences (not gaps)

- `/settings/*`, `/services/*`, `/usage/*`, `/sync/*` buckets: upstream splits management into many per-key routes; we consolidate. Feature surface equivalent.
- `/[...omnirouteCatchAll]` proxy catch-alls — N/A to our architecture.
- MCP server: upstream ~110 tools (45 canonical + memory/skill/github/pool/gamification/plugin/notion/obsidian/corpus/RTK modules); we ship ~88 — remaining tools live inside modules we may or may not want; not counted as endpoint gap.

## Proposed follow-up specs (ranked by user value)

| Spec | Scope | Why |
|------|-------|-----|
| SPEC-125 | OAuth local-CLI credential import (codex, cursor, kiro, agy, cliproxy, paste-credentials) | Highest daily-use value: lets users import existing CLI creds in one click instead of manual OAuth dance |
| SPEC-126 | cli-tools depth — per-tool settings for the ~25 CLIs + apply/backups/logs | Dashboard feature parity; heavy but mechanical |
| SPEC-127 | Agent bridge + traffic inspector | Distinct upstream capability (intercept local agents, inspect traffic) — big lift, TLS-intercept complexity; evaluate need first |
| SPEC-128 | Per-provider passthrough `/providers/{p}/chat/completions` + embeddings/images/voices | Lets clients target one provider directly — useful for debugging/comparisons |
| SPEC-129 | a2a task lifecycle (tasks/{id}, cancel, history) + gamification depth | Small; completes areas we already have |
| SPEC-130 | provider auth apply-local/export + interception-rules + refresh flows | Mid-value ops tooling |

Skipped: Electron (user decision), omniglyph/alpha (experimental upstream), cloud sync (upstream SaaS tie-in we don't operate).
