# Feature Comparison — OmniRoute 3.8.52 vs LLMRouter

Scope decision (owner): docker-first deployment → proxy/MITM, tunnels,
Electron desktop, CLI subscription auth are **out of scope** by design.
Everything on the gateway/routing core is ported faithfully (same semantics
as the upstream `open-sse` code), not reimplemented loosely.

## Ported 1:1

| Area | Upstream | LLMRouter |
|---|---|---|
| Provider registry | `providers.json` (~288 entries) | same file, same ids/formats |
| Gateway formats | OpenAI/Claude/Gemini/Responses + SSE | `/v1/*` + `/v1beta/*` |
| Error sanitization | `toJsonErrorPayload` | `ErrorSanitizer` |
| Refusal/phase SSE fields | yes | yes (SPEC-075) |
| Cache-creation tokens | counted in prompt total | same |
| anthropic rate-limit headers | propagated | same + `usage-quota` probe |
| bedrock same-role merge | yes | same |
| notion text sanitize | yes | same |
| Custom per-connection headers | `connectionCustomHeaders.ts` + `upstreamHeaders.ts` | `CustomHeaders` — same forbidden sets (SPEC-077) |
| Typed availability | `providerAvailability.ts` | `ProviderAvailability` — terminal statuses trusted ≤24 h (SPEC-076) |
| Stale combo refs | `staleModelRefs` + prune | `StaleComboRefs` — flag + opt-in prune (SPEC-076) |
| Quota-window scoring | quota-aware strategy | `RemainingQuotaScoreAsync` — window + daily/monthly fractions |
| Auto-router strategies | `routerStrategy.ts` + `nadirStrategy.ts` | `AutoRouter` — rules/score/cost/latency/sla/lkgp/nadir (SPEC-078) |
| Compression engines | 8 engines + framework | ported; llmlingua/omniglyph fail-open stubs (SPEC-034) |
| Combos | CRUD, test, reorder, duplicate, builder, defaults | all present |
| Model cooldowns / fallback chains / routing decisions | yes | yes (SPEC-070) |
| Provider ops | test/test-batch/bulk/validate/synced-models | yes (SPEC-068/071) |
| Quota windows / credential expiration / tags / policies / sessions / translator | yes | yes (SPEC-073) |
| auto/* virtual combos | yes | yes (SPEC-072) |
| Memory backends | kv/obsidian/notion | same (SPEC-033) |
| RTK filters | 8-filter catalog | same (SPEC-033) |
| Files/Batches | yes | yes (SPEC-040) |
| Keys/quota advanced | groups, regenerate, reveal, limits, plans, preview | yes (SPEC-041) |
| Usage analytics | ~7 endpoints + pages | same (SPEC-036) |
| CLI credential import | claude/codex/cursor/kiro/trae/zed/agy | same + LLMRouter scanners (SPEC-035) |
| Jobs scheduler | yes | yes (SPEC-038) |
| DB backup/export/import | yes | yes (SPEC-037) |
| Log export destinations | yes | yes (SPEC-042) |
| Rate limiting | yes | sliding-window (SPEC-039) |
| Playground extras | improve-prompt, presets, simulate-route | yes (SPEC-043) |
| Evals | suites/cases/runs + judges | yes (SPEC-052) |
| A2A + Conductor | task lifecycle + fleet | in-process executor (SPEC-053) |
| Session pools | yes | DB-backed pool (SPEC-049) |
| Prompt cache | yes | replay identical requests (SPEC-045) |
| Settings ops | purge, system-prompt, tiers, thinking-budget | yes (SPEC-046) |
| Settings routing | ip-filter, payload/reasoning/task rules, free-proxies | yes (SPEC-047) |
| Relay / vscode tokens | yes | yes (SPEC-048/051) |
| CLI device login | yes | yes (SPEC-050) |
| OpenAPI explorer | yes | yes (SPEC-044) |
| i18n | en/pt-BR/es | same bundles + locale fallback |

## Out of scope (intentional)

Electron desktop, MITM proxy/tunnels (cloudflared/ngrok/tailscale), CLI
subscription oauth to providers (codex/claude/cursor/zed/agy sign-in),
sidecar services (bifrost/cliproxy/dario/mux/openwa/llmlingua-server),
gamification/radar/media/bots integrations, WebDAV handler (our obsidian
backend reads the vault directly).

## LLMRouter-only

- `InternalChat` (in-process dispatch for background executors), typed
  `SqliteIdent` for backup SQL, `HotCache`/`UsageWriter` throughput layer,
  docker image + single-binary publishing, `LLMR_SYNC_WRITES` test hook.

## Analysis docs

- `docs/analysis/upstream-gap-scan.md` — original full gap scan
- `docs/analysis/core-gap-v3.8.52.md` — core/gateway gap list
- `docs/analysis/omniroute-3.8.52-delta.md` — v3.8.52 commit delta
- `docs/analysis/feature-matrix.md` — per-feature status
