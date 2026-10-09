# SPEC-052 — Evals

Upstream: `evals`, `evals/suites*`, `analytics/evals` page. LLMRouter's
evals page currently shows derived combo scoring (SPEC-036) — this adds
real suites.

## Scope
- `evalSuites`/`evalRuns`/`evalCases` tables: suite {id,name,cases[]},
  case {input,expect(contains|regex|json-schema|judge-prompt),weight},
  run {id,suiteId,target(combo|model),startedAt,results,score}.
- `Core/Evals/EvalRunner.cs` — runs each case through the gateway against
  the target, scores per-case, writes run with per-case verdicts.
- Endpoints: CRUD `/api/evals/suites`, `POST /api/evals/suites/{id}/run`,
  `GET /api/evals/runs`, `GET /api/evals/runs/{id}`.
- UI: extend `/dashboard/analytics/evals` — suites CRUD, run target picker,
  run history + per-case results expand.
- Tests: tiny suite against a stub provider produces run with expected
  per-case pass/fail.
