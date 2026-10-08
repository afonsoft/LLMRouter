# SPEC-001 — Foundation: solution, design system, shell, auth, core gateway

## Goal

Bootstrap the LLMRouter repo as a .NET 10 hosted Blazor WebAssembly app that is
visually and functionally compatible with OmniRoute's dashboard shell, plus a working
OpenAI-compatible gateway core (API keys, provider connections, combos with fallback,
usage logging).

## Upstream sources (pinned commits — see .specs/README.md)

- `src/app/globals.css` → `src/LLMRouter.Client/wwwroot/css/app.css` (tokens verbatim)
- `src/app/layout.tsx` → `wwwroot/index.html` (theme pre-paint script, fonts-loaded script)
- `src/shared/constants/sidebarVisibility/sections.ts` → `Nav/SidebarModel.cs` (all 96 items, 10 sections, groups)
- `src/shared/components/{Sidebar,Header,Breadcrumbs,NotificationToast,CommandPalette,ThemeToggle,LanguageSelector,OmniRouteLogo}.tsx` + `layouts/{DashboardLayout,AuthLayout}.tsx` → `Layout/` + `Components/`
- `src/app/login/page.tsx` → `Pages/Login.razor`
- `src/lib/db/schema.js` → `src/LLMRouter.Core/Data/` (entities + DbContext, same table/column names)
- `open-sse/config/providers/index.ts` (288 providers) + `src/shared/constants/providers/*` → embedded JSON in `LLMRouter.Core/Registry/`
- `open-sse/services/{model,combo,provider}.js` → `Routing/`
- `open-sse/translator/formats/{openai,claude,gemini}.js` → `Translation/` (core paths only this spec)

## Deliverables

### Solution

```
LLMRouter.sln
common.props                     — LangVersion 14, Nullable enable, net10.0
src/LLMRouter.Core/              — domain + EF Core SQLite + registry + routing + translators
src/LLMRouter.Server/            — ASP.NET Core host (serves WASM, /v1 gateway, /api management)
src/LLMRouter.Client/            — Blazor WASM (Tailwind v4 build step)
src/LLMRouter.Shared/            — DTOs
tests/LLMRouter.Tests/           — xUnit + Shouldly
.github/workflows/ci.yml         — dotnet build + test, node/tailwind step
```

### Design system (verbatim port)

- `wwwroot/css/app.css`: all `:root`/`.dark` tokens from upstream globals.css
  (colors, shadows, radii, `--grid-line`/`--grid-size`, font stacks), `body::before`
  graph-paper wallpaper, `custom-scrollbar`, `.dashboard-sidebar-*` rules.
- `wwwroot/fonts/material-symbols-outlined.woff2` + `@font-face` (self-hosted, same
  `fonts-loaded` gate so ligature names never flash).
- `index.html`: the exact pre-paint theme script (reads `localStorage.theme` zustand
  shape `{"state":{"theme":...}}`, applies `.dark` before first paint) and the
  `fonts-loaded` script.
- `ThemeService` writes the same localStorage key/shape; dark/light/system options.

### Shell

- `Sidebar`: full 96-item nav from `sections.ts` (sections OmniProxy, Analytics, Costs,
  Monitoring, Dev Tools, Agentic Features, Other Features, Configuration, Help — each
  with groups/items/icons/subtitles), accordion expansion persisted to
  `localStorage["sidebar-expanded-sections"]`, collapse toggle persisted to
  `sidebar-collapsed`, search box filtering items, pin sections/items (localStorage
  `sidebar-pinned-*`), active state = `bg-primary/10 text-primary` + filled icon,
  version footer + shutdown/restart buttons.
- `Header`: page icon+title+description derived from current route (longest-prefix
  match over nav items), Ctrl/⌘+K command palette button, `LanguageSelector`,
  `ThemeToggle`, logout button → `POST /api/auth/logout` → `/login`.
- `DashboardLayout`: desktop sidebar (always visible lg+), mobile slide-in sidebar with
  overlay, `main` scroll area `p-4 sm:p-6 lg:p-10`, `max-w-[3840px]` wrapper,
  Breadcrumbs, NotificationToast stack (top-right, success/error/warning/info styles).
- `CommandPalette`: fuzzy search over all nav items + navigation, ⌘K binding.
- `AuthLayout` + `Login` page: centered card, OmniRouteLogo mark, password field,
  calls `/api/settings/require-login` then `/api/auth/login`; first-run creates the
  admin password (`hasPassword=false` → setup mode).

### Persistence (LLMRouter.Core/Data)

EF Core SQLite at `%LOCALAPPDATA%/LLMRouter/llmrouter.db` (or `LLMROUTER_DB_PATH`),
`EnsureCreated` at startup. Tables identical to upstream schema.js: `_meta`,
`settings`, `providerConnections`, `providerNodes`, `proxyPools`, `apiKeys`,
`combos`, `kv`, `usageHistory`, `usageDaily`, `requestDetails` + same indexes.

### Gateway (LLMRouter.Server + Core)

- API-key auth: `Authorization: Bearer <key>` or `x-api-key`, validated against
  `apiKeys` (isActive, accessRestricted/accessAllow model allow-list). Mount all
  gateway endpoints at BOTH `/v1/*` and `/api/v1/*`. CORS `*` with OPTIONS handler.
- `POST /v1/chat/completions`: parse body → resolve model (`provider/model` →
  connection of provider; bare name → combo lookup → model aliases → prefix
  inference) → combo cascade fallback (ordered; round-robin+sticky for
  `kind="round-robin"`; capability auto-switch reorder) → build upstream request per
  provider transport (baseUrl/chatPath, format, authHeader, headers, urlSuffix,
  unsupportedParams strip, forceStream) → stream SSE chunks through (translate
  claude→openai on the fly for `format:"claude"` providers) → record `usageHistory`
  + `requestDetails` + `usageDaily`.
- `POST /v1/messages`: Anthropic Messages API → translate request claude→openai →
  run same pipeline → translate response (and SSE stream) back to claude events.
  `POST /v1/messages/count_tokens`: naive token estimate endpoint (compat).
- `GET /v1/models`: union of every active connection's provider models
  (`provider/model` ids) + every combo name as a virtual model entry.
- `POST /v1/embeddings`: passthrough to openai-format providers.
- Fallback semantics: retry on 429/5xx/timeout/network → next model; honor
  `Retry-After` by preferring other models first; return last error if all fail.

### Management API (`/api/*`, cookie-auth)

- `GET /api/settings/require-login` → `{requireLogin, hasPassword, setupComplete, authenticated}` (same shape as upstream)
- `POST /api/auth/login|logout`, `GET /api/auth/status`
- `GET/PUT /api/settings`
- `GET /api/providers` (registry + UI metadata + connection status), `GET /api/providers/{id}`
- `GET/POST/DELETE /api/provider-connections` (+ `PUT /{id}`, `POST /{id}/test`)
- `GET/POST/PUT/DELETE /api/keys` (generate `sk-...` keys)
- `GET/POST/PUT/DELETE /api/combos`
- `GET /api/usage` (summary + time series), `GET /api/usage/history`
- `GET /api/logs` (requestDetails), `GET /api/logs/{id}`
- `GET /api/models`
- `GET /api/health`, `GET /api/version`
- `POST /api/shutdown`, `POST /api/restart` (graceful)

### Pages (functional this spec)

Home (stat cards + quick links), Endpoint & Key (base URL copy box + key list + create),
Providers (grid grouped by category, connection badges), Provider detail (list/add/edit/
delete apikey connections + test button), Models & Combos (model browser + combo
create/edit with model ordering), Usage (summary cards + history table + daily chart),
Logs (request details table + expand JSON), Playground (chat against the gateway using
any connection key — picker of models/combos), Settings/General (password, base URL,
defaults), Settings/Appearance (theme + language). All other nav routes →
`PlaceholderPage` showing the correct icon/title/subtitle + "em breve" note so nav is
100% complete.

## Acceptance criteria

- `dotnet build` clean, `dotnet test` green.
- `dotnet run --project src/LLMRouter.Server` serves: login → dashboard shell visually
  matching OmniRoute (same colors/fonts/sidebar/header), all nav items routable.
- `curl -H "Authorization: Bearer <key>" localhost:PORT/v1/chat/completions` against a
  configured OpenAI-compatible connection returns a completion; `stream:true` streams
  SSE; a combo name resolves through fallback.
- `POST /v1/messages` with an Anthropic-format body returns Anthropic-format JSON.
- Dark/light/system theme works without flash on reload; EN/PT-BR/ES switch live.

## Tests

- `ModelResolverTests` — parse/alias/prefix inference incl. `claude-*`→anthropic,
  `gpt-*`→openai, codex rules, `provider/model`, combos are NOT resolved here.
- `ComboPlannerTests` — round-robin rotation + sticky limit, fallback order,
  capability reorder (vision request floats vision model).
- `ClaudeTranslatorTests` — system→message, content blocks, tool_use/tool_result,
  tools mapping, response translation, SSE delta translation.
- `ApiKeyAuthTests` — bearer + x-api-key, inactive key → 401, allow-list restriction.
- `GatewayEndpointsTests` — WebApplicationFactory: fake upstream (HttpMessageHandler)
  verifies passthrough, fallback on 429, usage row written.
- `RegistryTests` — 288 providers load, alias resolution, UI catalogs load.
