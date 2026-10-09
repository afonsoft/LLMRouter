# SPEC-053 — A2A tasks + Conductor lifecycle

Upstream: `a2a/{tasks(+id,+cancel,+history),status}`,
`conductor/{ask,fleet,tasks(+id,+cancel)}`. LLMRouter has one-shot `a2a`
forward and `conductor/{run,workflows}` — no task lifecycle.

## Scope
- `a2aTasks` table: {id,agent,payload,state(queued|running|done|failed|
  cancelled),result,createdAt,finishedAt}; background executor drains queue.
- Endpoints: `POST /api/a2a/tasks`, `GET /api/a2a/tasks`,
  `GET /api/a2a/tasks/{id}`, `POST /api/a2a/tasks/{id}/cancel`,
  `GET /api/a2a/tasks/{id}/history`, `GET /api/a2a/status`.
- Conductor: `conductorTasks` table + `POST /api/conductor/ask`
  (natural-language task → decompose via configured model → steps);
  `GET /api/conductor/tasks(+id)`, `POST .../{id}/cancel`,
  `GET /api/conductor/fleet` (registered worker/endpoint status).
- UI: `/dashboard/a2a` and `/dashboard/conductor` pages — task tables with
  state chips, cancel buttons, result expand.
- Tests: enqueue task → polled to done with result; cancel mid-run.
