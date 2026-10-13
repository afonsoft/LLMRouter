# SPEC-143 — Generation detail sheet + I/O logging opt-in
- `/dashboard/logs`: click row → sheet com overview completo + Provider
  Responses waterfall (proxyLogs por correlationId) + raw JSON + replay link.
- `settings.observability.ioLogging` opt-in: guarda prompt/completion
  (usage row novo campo ou requestDetails); default metadata-only.
- API: `GET /api/v1/generation?id=` (meta) + `/api/v1/generation/content` (só quando flag on).
