# OmniRoute v3.8.52 delta — core-gateway re-validation

Upstream clone moved `release/v3.8.52` from `02f2f00e7` → `7510677f2` (+27 commits, ~628 LOC net in src).
Cross-referenced against LLMRouter main (post PRs #61–#69). Scoped to gateway/provider/combo core —
proxy/CLI-auth/Electron/media items dropped per the docker pivot.

## New upstream features worth porting

| Upstream item | Our status | Action |
|---|---|---|
| `auto-combo` router strategies: `rules` (16-factor), `score`, `cost/eco`, `latency/fast`, `sla-aware` (p95/error/cost SLOs), `lkgp`, `nadir` (external decision API) | Our `auto` is a simplified 3-factor composite; no `routerStrategy` field on combos | Port strategy layer minus `nadir` (external API — skip unless asked). `sla-aware` needs p95/error telemetry we already collect in usageHistory |
| `combos` auto-prune of stale steps after model sync (opt-in flag) | Not implemented | Small: post-sync hook deleting combo models no longer in SyncedModels |
| Typed provider availability states + preserve account cooldown in weighted availability; terminal quota >24h stays stale | Breaker/cooldown exist but untyped; stale-quota edge not handled | Port state enum + stale rule |
| Custom headers sent on model discovery (same forbidden-header rules as chat) | `Provider.Headers` exists for chat; model sync path doesn't send them | Small fix in model sync/refresh |
| `toJsonErrorPayload` sanitization — stack traces + credential-shaped fields stripped from upstream error bodies before reaching client | `WriteError` forwards raw upstream `errBody` | Port: strip `stack`, keys matching token/password/secret/api_key, file paths |
| Responses API fidelity: preserve `phase` on messages, refusals across empty SSE snapshots, buffered failure classification | Basic translator; no phase/refusal handling | Port to `Translators` + `SseState` |
| `usage`: cache-creation tokens preserved through translation | `ExtractUsage` reads prompt/completion only | Add cache_creation/cache_read token fields where the source format exposes them |
| Anthropic API-key rate-limit headers → provider window costs display | Not implemented | Optional; parse `anthropic-ratelimit-*` headers on responses |
| bedrock executor: merge consecutive same-role messages | No bedrock executor in our registry | Skip unless we add bedrock |
| `quota` USD recorded-cost matching (indexed nearest-unused) | No recorded-cost matching — we record what the upstream reports | Only needed if we adopt USD-quota matching; skip for now |
| Notion `sanitizeNotionAssistantText` | Notion backend ported; check sanitize parity | Small port if absent |

## Performance findings (hot path, per `/v1/chat/completions` request)

Sequential DB round-trips before dispatch (SQLite): auth key, settings row, skills-disabled KV +
**recursive SKILL.md filesystem scan**, keyRow (2nd key query), rate limits, daily quota, chaos rules,
combo lookup, compression assignment+combo, plugin registry, tier rules, oneproxy, prompt-cache,
then `ResolveAsync` (breaker + quota-windows + conns + cooldowns + session-pools) — ~15+ reads + at least
2 synchronous writes (`ReportConnectionAsync`, `LogUsageAsync`) per request.

Real bug found: `_ = AuditAsync(db, …)` runs fire-and-forget on the request-scoped `DbContext` while the
request keeps using it → concurrent DbContext access (EF throws intermittently under load). Same for the
audit write itself (read-modify-write 1000-entry KV blob — O(n) serialization per call, serializes on the
kv row).

Streaming path is fine (line-by-line SSE passthrough with translation, `ResponseHeadersRead`).

→ `SPEC-074` covers: scoped-context audit fix, hot-path MemoryCache (settings/keys/combos/rules/plugins/skills,
invalidated on writes), `AsNoTracking` reads, usage-write offloading to a background channel, indexes on
usageHistory, and a before/after load-test harness.
