# SPEC-059 — Version manager + power

Upstream: `version-manager/{check-update,install,start,stop,status,restart}`,
`restart`, `shutdown`. Self-update and graceful restart from the UI.

## Scope
- `GET /api/version-manager/status` — current version (assembly + git sha),
  channel, runtime info.
- `GET /api/version-manager/check-update` — compares against releases feed
  (GitHub releases API on afonsoft/LLMRouter); returns latest+changelog URL.
- `POST /api/version-manager/install` — downloads the release asset for the
  runtime, unpacks to a staging dir (verify checksum); recorded as pending
  apply.
- `POST /api/restart` — graceful shutdown flag; process exits expecting the
  supervisor (docker/systemd) to restart it; on boot, pending apply swaps
  binaries.
- `POST /api/shutdown` — exit(0) for containerless control.
- UI: `/dashboard/version` page — status card, check-update, install,
  restart/shutdown with confirm.
- Tests: status returns version; check-update parses feed (stubbed);
  install rejects bad checksum.
