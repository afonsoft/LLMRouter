# SPEC-065 — Bots & niche integrations

Upstream: `copilot/chat`, `issue-agent/runs`, `telegram/update`,
`vnc-session/*`, `cursor-cli/[...path]`, `dahl/tokens`. Optional bots that
drive the app from chat ops channels.

## Scope
- `POST /api/copilot/chat` — dashboard chat that can invoke read-only
  internal APIs (status, usage, list providers) via a configured model;
  returns answer + tool trace.
- `issueAgent` — table of runs {id,source(github|slack),issue,state,prUrl};
  `GET /api/issue-agent/runs`; trigger endpoint launches a Devin-style
  fix flow against configured repo (uses existing conductor).
- `POST /api/telegram/update` — webhook for a bot token: commands /status,
  /usage, /providers → replies via Telegram sendMessage.
- `vnc-session` — optional noVNC sidecar (depends on SPEC-061 services):
  start/stop/status for a browser-desktop view page.
- `cursor-cli/[...path]` — passthrough proxy for Cursor CLI API shape.
- `dahl/tokens` — compat token issue/verify for the dahl client.
- Tests: copilot answers with stub model; telegram update → command reply;
  cursor-cli forwards request.

## Non-goals
- Full Telegram command suite beyond status/usage/providers.
