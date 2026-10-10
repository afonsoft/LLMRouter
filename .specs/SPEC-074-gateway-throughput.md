# SPEC-074 — Gateway throughput for high request volume

Goal: make `/v1/chat/completions` (and friends) sustain many concurrent requests — docker deployment
target. Remove per-request serialization: repeated sequential DB reads, per-request filesystem scans,
fire-and-forget work on the request-scoped DbContext.

## Invariants

- Response semantics unchanged: same status codes, bodies, headers, cascade order.
- Usage records stay complete and correct (no dropped/garbled usageHistory/chat_sessions writes).
- Caches must invalidate on writes through our own endpoints (settings, combos, keys, rate limits,
  plugins, skills toggle, quota windows, session pools) and carry a short TTL as a safety net.

## Changes

1. **Concurrent DbContext fix**: `Extras.AuditAsync` (and any `_ = …(db, …)` fire-and-forget inside
   request handlers) must run on its own scope via `IServiceScopeFactory`, never on the scoped `db`.
   Convert audit KV blob to append-lite (cap list read at write time unchanged, but off request path).
2. **Hot-path read cache** (`MemoryCache`, ~30s TTL + explicit invalidation on mutation endpoints):
   - `PricingService.SettingsDataAsync` → `SettingsCache` (invalidate in every settings write).
   - `AuthenticatedKey` result → key→(valid, accessRestriction) cache.
   - `db.Combos` by-name lookup + `RateLimits.Enabled` list + `PluginHooks.RegisteredAsync` +
     `SessionPools`/`QuotaWindows` inside `ResolveAsync`.
   - `ToolsEndpoints.SkillPromptTextAsync` → cache the built string (skills disabled KV + dir mtime).
3. **AsNoTracking** on the read-only hot queries above.
4. **Usage write offload**: `UsageWriter` singleton — bounded `Channel<UsageWrite>` (cap ~10k, drop
   audit-style extras on overflow, never drop usageHistory rows — fall back to inline write).
   `LogUsageAsync`, `ReportConnectionAsync`, KeyQuota credit + ChatSession upsert batch via a
   `BackgroundService` flusher (every 200ms or batch≥50). Tests keep a synchronous path via
   `UsageWriter.FlushAsync` or direct service call.
5. **Indexes** (EF `[Index]`/modelBuilder): `usageHistory(timestamp)`, `usageHistory(keyId)`,
   `usageHistory(provider)`, `chatSessions(keyId,model)`, `providerConnections(provider,isActive)`.
6. **Load-test harness**: `tests/load/` — Node/Python or `dotnet run` stub upstream + wrk2/bombardier-
   style script (whatever's installed; else a small .NET console) measuring RPS + p50/p95/p99 against
   a stub upstream for: cold auth, cache-warm steady state, N=500 concurrent. Record before/after
   numbers in the spec's PR.

## Tests (xUnit)

- Audit async call no longer shares request DbContext (concurrent requests don't throw
  "second operation started").
- Settings cache: write via endpoint → next read reflects it (invalidation) — no stale read.
- Usage offload: N requests → all usageHistory rows land (flush deterministically in test).
- Index DDL created on fresh DB (`db.Database.GetCreateScript` or pragma check).
- Existing 341/341 stays green.

## Out of scope

- Nadir external strategy, fusion/pipeline strategy internals, provider live probing.
- Kestrel/connection-limit tuning knobs (document if needed, no changes unless bench shows need).
