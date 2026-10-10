# SPEC-079 — SonarCloud issue sweep (afonsoft_LLMRouter)

Sweep of the open issue list on SonarCloud (~890 open issues). This spec fixes
every BUG and VULNERABILITY class that's actionable plus the cheap mechanical
CODE_SMELL classes; the remaining bulk classes are triaged at the end.

## Fixed

- **S8949 (≈98)** — `ctx.RequestAborted` passed to async calls (EF, IO, HTTP)
  across McpTools, GatewayEndpoints, VscodeEndpoints, FileEndpoints,
  RelayEndpoints, ProtocolEndpoints, ForwardProxy.
- **S2077 (5)** — backup/export SQL no longer interpolates raw names: strict
  `SqliteIdent` whitelist `[A-Za-z0-9_]` + `VACUUM INTO` bound parameter.
- **S6444 (83)** — all `new Regex`/`Regex.*` static calls get a 500 ms timeout
  (ReDoS bound) in ErrorSanitizer, Engines, PromptGuard, DocsEndpoints,
  MemoryStore, RoutingOps, CliCredentialScanner, RtkFilters, CavemanRules,
  CompressionEndpoints, MemorySearch.
- **S3923 (3)** — identical-branch ternaries removed/fixed
  (`isArr ? text : text`, `_err ? body : body` → success message,
  `? item : item` → `EnsureSuccessStatusCode()`).
- **S2583** — dead null path in `LenFor` (`return 0`).
- **S2259** — `m?["tool_call_id"]`/`m?["content"]` null-guards.
- **S3887 (2)** — `ModeToEngines` → `IReadOnlyDictionary` property;
  `OAuthService.Sessions` → private + `TryGetSession`/`RemoveSession`.
- **S3903** — `FrameworkAssets` moved out of Program.cs into
  `LLMRouter.Server` namespace (own file).
- **S7044** — `Connect.razor` validates the `Token` route param
  (`^[A-Za-z0-9_-]+$`) before path interpolation.
- **javascript:S9383 (2)** — sw.js cache puts get `.catch(() => {})`.
- **Web:S5254** — `offline.html` gets `lang="en"`.
- **css:S4649** — `Material Symbols Outlined` gets `sans-serif` fallback.
- **docker:S6471** — runtime stage runs as `USER app` (+ `chown app:app /data`).
- **S6580 (11)** — `DateTime.TryParse`/`TryParse` with `InvariantCulture`.
- **S108 + S2486 (~46)** — empty catch blocks annotated
  `/* best-effort: failure is non-fatal */`.

## Triaged (left open, documented)

- **S5332 (3)** — `http://` defaults are for LAN/localhost provider discovery;
  https is honored when configured. Intended behavior.
- **S5693 (2)** — Blazor `OpenReadStream` caps are set (50/64 MB); upload size
  is additionally bounded by `files` table storage.
- **S2583/S3923 (remaining)** — Sonar flow-analysis false positives on
  `JsonArray.Count` checks (toolCalls/parts are populated conditionally).
- **S127 (8)** — for-loop index mutation is the design of the sanitizer's
  skip-ahead decoder; converting to `while` adds risk with no gain.
- **S3776 (109), S1192 (159), S3358 (68), S1172, S3267, S3260, S1075, S8969**
  — refactor-style smells; deliberately not touched in a correctness sweep.
