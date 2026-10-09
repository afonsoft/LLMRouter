# SPEC-035 — CLI credential import

Port of upstream OmniRoute's onboarding feature: detect locally-installed LLM
CLI tools (claude, codex, cursor, kiro, trae, zed, agy, command-code, opencode)
and import their stored credentials as provider connections.

## Scope
- `Core/Extras/CliCredentialScanner.cs`: scans a candidate-file map under
  `$HOME` (overridable for tests), extracts api keys / oauth tokens per tool,
  returns masked findings + provider hint + source path.
- Endpoints (all under /api, cookie-authed):
  - `GET /api/cli-credentials/scan` → `{ findings: [{tool, provider, key, masked, path}] }`
  - `POST /api/cli-credentials/import` `{ ids: ["tool:index", ...] }` → creates
    providerConnections (Data.apiKey / Data.accessToken), skips duplicates of
    the same provider+key, returns `{ imported: n }`.
- UI: `CliImportCard.razor` — scan results checklist + Import button;
  embedded in Onboarding step 1 and `/dashboard/cli-tools`.
- Tests: scanner over a fixture HOME; import creates connections + dedup.

## Non-goals
- OS keychain reading (zed stores creds there) — file-based creds only.
- Per-tool "point CLI at LLMRouter" config writers (separate spec).
