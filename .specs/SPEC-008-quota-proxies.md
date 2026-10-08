# SPEC-008 — Quota tracker, Token Saver, Proxy Pools

## Goal

Three 9router pages and their backends: `/dashboard/quota`, `/dashboard/token-saver`,
`/dashboard/proxy-pools`.

## Upstream sources

- 9router `src/app/(dashboard)/dashboard/{quota,token-saver,proxy-pools}/**`
- `src/app/api/{quota,proxy-pools,proxy-fallback}/**` (both repos)
- `src/lib/{quota/,freeProxyProviders,proxyPoolEgressObservation*,proxyHealth*,upstream-proxy*}`
- OmniRoute `/dashboard/quota` (providerQuota) + `/dashboard/system/proxy`

## Scope

- **Quota**: per-provider quota limits from upstream APIs (`transport.usage.urls`
  fetch → remaining/used/limit display, reset time), manual quota caps per
  connection (daily/monthly token limits → cascade skips exhausted connections).
- **Token saver**: compression/adjacent settings (whitespace cleanup, dedup toggles)
  + savings stats panel (tokens saved vs baseline measured from logged requests).
- **Proxy pools**: `proxyPools` CRUD — egress HTTP/SOCKS5 proxies (socks-proxy-agent
  equivalent: .NET `SocketsHttpHandler` with `Proxy`), test each proxy (latency to a
  target URL), assign pools to connections (`providerSpecificData.proxyPoolId`),
  round-robin pool selection per request, mark bad proxies.

## Tests

- Quota math + skip-on-exhaust in cascade; proxy selection round-robin; pool test
  via fake handler.
