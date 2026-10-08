# SPEC-014 — MITM relay + Traffic inspector

## Goal

The intercept/proxy features: `/dashboard/tools/traffic-inspector`,
`/dashboard/system/proxy` (mitm), `pxpipe`, request relay viewer.

## Upstream sources

- `src/mitm/**` (cert/, dns/, handlers/), `src/app/(dashboard)/dashboard/tools/traffic-inspector/**`, `src/app/api/{relay,middleware}/**`
- 9router `src/app/(dashboard)/dashboard/{mitm,pxpipe}/**` + `src/app/api/pxpipe/**`
- `src/lib/{proxyRelay/,inspector/,middleware/}`

## Scope

- System proxy mode: ASP.NET middleware that accepts absolute-URI proxy requests
  (CONNECT not required — plain forward proxy for http/https via `Proxy-Authorization`
  or none), logs flows, forwards to the gateway pipeline when the target is an LLM
  endpoint.
- Self-signed CA generation (`src/mitm/cert`) — .NET `CertificateRequest`; download
  endpoint + install instructions shown on the mitm page.
- Traffic inspector: captured flow list (host, method, status, duration, size),
  detail view (request/response headers+body), filter/search, toggle capture.
- Relay: replay a captured request through a different provider/combo
  (`/api/relay`), diff view.
- pxpipe page (9router): media pipeline status view — port its panels verbatim.

## Tests

- Forward-proxy request flow; capture + detail retrieval; cert generation produces
  parseable X509.
