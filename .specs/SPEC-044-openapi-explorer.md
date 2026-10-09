# SPEC-044 — OpenAPI explorer

Upstream: `openapi/{spec,try}` + `docs/api-explorer` page. `ApiEndpoints`
page is a static list.

## Scope
- `GET /api/openapi/spec` — generated OpenAPI 3.1 doc: enumerate route table
  (IEndpointRouteBuilder metadata) + hand-written request/response schemas
  for the /v1 compat surface; cached.
- `POST /api/openapi/try` `{path,method,body?}` — server-side dispatch
  through the app's own pipeline (TestServer-style self-call), returns
  status+body+latency.
- UI: rewrite `ApiEndpoints.razor` as explorer — grouped tree from the spec,
  per-endpoint "try it" form building the request from the schema.
- Tests: spec returns paths object containing /v1/chat/completions; try-it
  against a GET endpoint returns 200.
