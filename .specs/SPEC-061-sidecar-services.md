# SPEC-061 — Sidecar services lifecycle

Upstream: `services/{9router,bifrost,cliproxy,dario,mux,openwa,llmlingua}/
{install,start,stop,update,auto-start,restart,status,provider-expose,
auto-restart-adopted}`, `local/redis/*`, `services/[name]/logs`. Largest
remaining gap — process management for optional sidecar daemons that add
capabilities (llmlingua unlocks SPEC-034 stub, bifrost = fallback gateway,
cliproxy = oauth proxy import).

## Scope
- `Core/Services/ServiceManager.cs` — service descriptor {id,name,binaryUrl,
  port,healthPath,args,env}; install downloads+verifies binary into
  {DbDir}/services/{id}/; start/stop via Process with adopted-process
  detection (port+health probe) for services started outside the app;
  logs ring buffer per service; auto-start + auto-restart flags persisted.
- Service catalog mirroring upstream names: 9router, bifrost, cliproxy,
  dario, mux, openwa, llmlingua, redis.
- `provider-expose` — register a running service as a providerConnection
  (openai-compatible base URL) in one click.
- Endpoints: `GET /api/services` (+status), `POST /api/services/{id}/install
  |start|stop|restart|update`, `GET /api/services/{id}/logs`,
  `PUT /api/services/{id}/auto-start|auto-restart`,
  `POST /api/services/{id}/provider-expose`,
  `local/redis/*` equivalents for the bundled redis.
- UI: `/dashboard/services` page rewritten — per-service card with state,
  install/start/stop, logs drawer, expose-as-provider button; wires
  llmlingua service to the SPEC-034 engine's `stable` flag.
- Tests: descriptor registry complete; adopt detection via stub health
  endpoint; provider-expose creates connection.
