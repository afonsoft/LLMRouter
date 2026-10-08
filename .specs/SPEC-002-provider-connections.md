# SPEC-002 — Provider connections management (full parity)

## Goal

Complete provider connection lifecycle: every auth type (apikey, oauth, freeTier,
noauth), provider nodes (openai-compatible-*, anthropic-compatible-*, claude-code-*),
proxy-aware connections, connection testing, embedded services.

## Upstream sources

- `src/app/(dashboard)/dashboard/providers/**` — providers grid, provider detail page
- `src/app/(dashboard)/dashboard/providers/services/**` — embedded services page
- `src/shared/components/{ProviderIcon,ProviderInfoCard,ProviderTestSlideOver,EditConnectionModal?,AddCustomEmbeddingModal,ManualConfigModal,NoAuthProviderCard,ConnectionTestButton,ConnectionTestModelField}.tsx` (9router equivalents in `~/repos/9router-src/src/shared/components/`)
- `src/shared/constants/providers/{apikey,oauth,noauth,local,upstream-proxy,web-cookie,audio,search,system,cloud-agent}.ts` — already embedded as `ui-providers.json`
- `src/app/api/providers/**`, `src/app/api/provider-nodes/**`, `src/app/api/provider-models/**`
- `open-sse/services/provider.js` — openai-compatible-*/anthropic-compatible-* node handling, `resolveOpenAICompatibleApiType`
- `src/lib/oauth/**` — OAuth service layer (flows themselves land in SPEC-011)

## Scope

### API

- `GET /api/providers` — every catalog entry with: ui metadata (name, icon, color,
  website, serviceKinds, category), backend transport presence, connection count /
  connected status.
- `GET /api/providers/{id}` — detail: registry entry + ui entry + its connections.
- `GET/POST/PUT/DELETE /api/provider-connections` — full CRUD; `data` JSON blob carries
  credentials (`apiKey`, `providerSpecificData`, `oauth` tokens etc.); `priority`
  ordering; `isActive` toggle.
- `POST /api/provider-connections/{id}/test` — hit `testKeyModelsUrl || modelsUrl ||
  baseUrl` upstream, record latency + status (OK/auth-fail/network) on the connection.
- `GET/POST/PUT/DELETE /api/provider-nodes` — custom nodes:
  `openai-compatible-<chat|responses>-<uuid>` (baseUrl user-defined),
  `anthropic-compatible-*`, `claude-code-*`; these participate in model resolution
  exactly like upstream (see `resolveOpenAICompatibleApiType`).
- `GET /api/provider-models?provider=` — registry model list + live fetch via
  `modelsUrl`/`modelsFetcher` when the upstream supports it (cache 5 min).

### Pages

- `/dashboard/providers`: card grid grouped by catalog category (Gateways, Frontier
  Labs, Inference Hosts, Enterprise Cloud, Regional, Specialty/Media — same grouping as
  `apikey/index.ts` barrels + oauth/noauth/etc. categories), search filter, connected
  badge, per-category tab counts.
- `/dashboard/providers/{id}`: provider header (icon/color/name/website/apikey url
  notice), connection cards (name, email, priority, status pill, test result,
  edit/delete), "Add connection" modal with fields per authType
  (apikey → key field + optional baseUrl override; oauth → "sign in" entry point that
  lands on SPEC-011 flow; noauth → name only).
- `/dashboard/providers/services`: embedded services list.
- Connection test slide-over matching `ProviderTestSlideOver.tsx` (pick model → run →
  show latency/result/streamed first tokens).

## Acceptance criteria

- Add/edit/delete/reorder/toggle connections on any apikey provider; custom
  openai-compatible node with user baseUrl works end-to-end through the gateway.
- Test button hits the upstream `modelsUrl` and renders green/red result.
- Models page shows live-fetched models for providers with `modelsFetcher`.

## Tests

- CRUD round-trip via WebApplicationFactory; node prefix resolution
  (`openai-compatible-chat-x` → chat transport); test endpoint uses injected
  HttpMessageHandler.
