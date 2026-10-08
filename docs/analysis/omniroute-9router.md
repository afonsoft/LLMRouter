# OmniRoute + 9router → LLMRouter — Migration Analysis

Pinned upstream: OmniRoute `61e07fb` (2026-10-08), 9router `ce4460e` (2026-10-08).

## Relationship

OmniRoute is a superset fork of the 9router lineage — same skeleton
(`open-sse/`, `mitm/`, `sse/`, `src/shared/`, `src/app/api/v1*`). We use
**OmniRoute as canonical** for design + features; 9router as the simpler reference
implementation and for its unique pages (pxpipe, basic-chat, token-saver, mitm).

## Scale

| | OmniRoute | 9router |
|---|---|---|
| LOC (src+open-sse) | ~546K | ~157K |
| Dashboard pages | ~70 dirs, 96 nav items / 10 sections | ~18 |
| API route groups | 104 | ~25 |
| Providers (registry) | 288 | 137 |
| Locales | 67 | ~15 |

## Design system (OmniRoute — adopted)

- Palette "OpenClaw × ClawHub": primary `#e54d5e`, accent `#6366f1`, hover `#c93d4e`/`#8b5cf6`,
  accent-light `#a855f7`; light bg `#f9f9fb`, surface `#fff`, border `rgba(0,0,0,.08)`;
  dark bg `#0b0e14`, surface `#161b22`, sidebar `#10141e`, border `rgba(255,255,255,.08)`.
- Fonts: `-apple-system,"SF Pro Text","SF Pro Display",system-ui` + mono
  `ui-monospace,"JetBrains Mono","Fira Code","SF Mono"`; icons Material Symbols Outlined (self-hosted woff2).
- Radii: card 14px, control 9px. Graph-paper wallpaper `body::before` 32px grid.
- Theme: `localStorage["theme"]` zustand shape `{"state":{"theme":"light|dark|system"}}`,
  `.dark` class on `<html>`, pre-paint script avoids FOUC.
- Sidebar: 264px, sections w/ accordion, search, pins, collapse; Header: icon+title+desc,
  ⌘K palette, language selector, theme toggle, logout.

## Data model (SQLite — mirrored 1:1)

`_meta`, `settings(id=1,data)`, `providerConnections(id,provider,authType,name,email,
priority,isActive,data,createdAt,updatedAt)`, `providerNodes`, `proxyPools`,
`apiKeys(id,key,name,machineId,isActive,accessRestricted,accessAllow,createdAt)`,
`combos(id,name,kind,models,createdAt,updatedAt)`, `kv(scope,key,value)`,
`usageHistory(timestamp,provider,model,connectionId,apiKey,endpoint,promptTokens,
completionTokens,cost,status,tokens,meta)`, `usageDaily(dateKey,data)`,
`requestDetails(id,timestamp,provider,model,connectionId,status,data)`.

## Request pipeline (from open-sse)

1. Inbound: `/v1/chat/completions` (openai), `/v1/messages` (claude), `/v1beta/*` (gemini), `/v1/responses`.
2. Auth: API key (`Bearer`/`x-api-key`) from `apiKeys`.
3. Resolve `model`: `provider/model` → provider; bare → combo name → model aliases
   (`kv`/settings) → builtin aliases → prefix inference (claude→anthropic, gpt-[56].→codex,
   gpt-*→openai, gemini-*→gemini, bedrock arn-ish, deepseek→openrouter, default openai).
4. Combo cascade: ordered fallback / round-robin(+stickyLimit); capability auto-switch
   reorders by vision/pdf/audio/video needs of the trailing user turn.
5. Connection pick: providerConnections of provider, isActive, priority order.
6. Executor by transport `format` (openai 264/288, claude 10, gemini 3, responses 4,
   special executors) — translate request, POST baseUrl, stream SSE, translate back.
7. Record `usageHistory` + `requestDetails` + `usageDaily` rollup.

## Provider registry (already ported as data)

`src/LLMRouter.Core/Registry/providers.json` = upstream `REGISTRY` (288 entries:
id, alias(es), format, executor, baseUrl, authType, authHeader, models[] w/ contextLength/
capabilities/pricing, oauth, transports…). `ui-providers.json` = 10 UI catalogs
(APIKEY 248, OAUTH 26, WEB_COOKIE 37, SEARCH 17, LOCAL 14, AUDIO 12, NOAUTH 10, CLOUD 3, UPSTREAM 2, SYSTEM 1).
