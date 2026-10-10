# Deployment

## Docker (primary target)

```bash
cp .env.example .env
docker compose up -d          # builds from Dockerfile
# or the published image:
docker compose up -d llmrouter-image   # ghcr.io/afonsoft/llmrouter:latest
```

- Port `20128` (dashboard + gateway on the same port).
- SQLite at `/data/llmrouter.db` — mount the `llmrouter-data` volume.
- Image runs as non-root `app` user (uid 1654); multi-arch amd64/arm64.

Minimal run without compose:

```bash
docker run -d -p 20128:20128 -v llmrouter-data:/data \
  ghcr.io/afonsoft/llmrouter:latest
```

## Single binary

```bash
dotnet publish src/LLMRouter.Server -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true -o dist
./dist/LLMRouter.Server serve
ASPNETCORE_URLS=http://+:8080 ./dist/LLMRouter.Server serve   # custom port
```

CLI: `serve` | `reset-password <pw>` | `version`.

## First boot

1. Open `http://host:20128` → create the admin password (first login accepts
   any non-empty password, then asks you to set it).
2. Add a provider connection (Providers page) — paste API key or run an OAuth
   flow (tokens page) — `test` it.
3. Create an API key (Keys page).
4. Create a combo or use `auto/best` directly as `model`.
5. Point clients at `http://host:20128/v1/chat/completions` with `Bearer sk-*`.

## Ops

- **Backups**: `GET /api/db-backups/export` (`.db`) or `export-all` (JSON);
  scheduled `db-backup` job writes `backups/llmrouter-*.json` (keeps 14).
- **Log export**: destinations file/webhook/s3, filters + scheduled runs.
- **Jobs**: `/dashboard/jobs` — proxy-pool-health, usage-prune, db-backup,
  log-export; enable/disable/run-now + run history.
- **Usage retention**: `settings.data.retention.usageDays` (default 30).
- **Rate limits**: `/api/rate-limits*` sliding-window rules + per-key
  rpm/tpm/dailyTokens.
- **Telemetry**: `usageHistory` + `requestDetails` per request; analytics at
  `/api/usage/*` and dashboard Analytics pages.

## Performance notes

- `HotCache` caches all config reads (TTL 5–60 s, write-through invalidation).
- `UsageWriter` serializes telemetry off the request path (channel of 10 k,
  inline fallback — usage is never dropped).
- Measured: ~251 rps / p50 25 ms at concurrency 40 against a stub upstream.
- Indexes: `usageHistory(ApiKey)`, `chatSessions(KeyId,Model)`,
  `providerConnections(Provider,IsActive)`, `quotaWindows(Provider)`.

## Health

`GET /api/status` (public), `GET /api/health/connections` (typed availability
per connection), `/api/providers/{id}/usage-quota` (upstream quota probe).
