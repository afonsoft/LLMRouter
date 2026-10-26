# SPEC-159 — Provider operations parity

Origin: OmniRoute v3.8.52 Providers page and provider operations APIs.

## Goal

Bring provider management closer to the upstream operational workflow without changing the
LLMRouter provider registry or secret storage model.

## Scope

- Group providers by auth/category with connected counts and configured-only filtering.
- Add batch test and validation actions with bounded concurrency.
- Show connection health, last test, cooldown, quota-window, and credential-expiration state.
- Add provider action summaries to the dashboard and Providers page.
- Preserve current provider CRUD and existing custom-endpoint behavior.

## Acceptance criteria

- A provider operator can search/filter, test a group, inspect failures, and re-enable a connection
  without leaving the catalog.
- Batch actions never expose credentials and do not block the request pipeline.
- Existing provider CRUD/API contracts remain compatible.

## Tests

- Unit tests for grouping, bounded batch execution, and state mapping.
- Endpoint tests for batch test/validation and action summaries.
- Browser coverage for configured-only and degraded states.
