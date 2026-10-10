# Deploy

## Docker (alvo principal)

```bash
cp .env.example .env
docker compose up -d          # builda a partir do Dockerfile
# ou a imagem publicada:
docker compose up -d llmrouter-image   # ghcr.io/afonsoft/llmrouter:latest
```

- Porta `20128` (dashboard + gateway na mesma porta).
- SQLite em `/data/llmrouter.db` — monte o volume `llmrouter-data`.
- Imagem roda como usuário `app` não-root (uid 1654); multi-arch amd64/arm64.

Execução mínima sem compose:

```bash
docker run -d -p 20128:20128 -v llmrouter-data:/data \
  ghcr.io/afonsoft/llmrouter:latest
```

## Binário único

```bash
dotnet publish src/LLMRouter.Server -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true -o dist
./dist/LLMRouter.Server serve
ASPNETCORE_URLS=http://+:8080 ./dist/LLMRouter.Server serve   # porta custom
```

CLI: `serve` | `reset-password <senha>` | `version`.

## Primeiro boot

1. Abra `http://host:20128` → crie a senha do admin (o primeiro login aceita
   qualquer senha não-vazia e pede para defini-la).
2. Adicione uma conexão de provider (página Providers) — cole a API key ou
   rode um fluxo OAuth (página Tokens) — teste.
3. Crie uma API key (página Keys).
4. Crie um combo ou use `auto/best` direto como `model`.
5. Aponte clientes para `http://host:20128/v1/chat/completions` com
   `Bearer sk-*`.

## Operações

- **Backups**: `GET /api/db-backups/export` (`.db`) ou `export-all` (JSON);
  o job `db-backup` grava `backups/llmrouter-*.json` (mantém 14).
- **Log export**: destinations file/webhook/s3, filtros + runs agendados.
- **Jobs**: `/dashboard/jobs` — proxy-pool-health, usage-prune, db-backup,
  log-export; enable/disable/run-now + histórico.
- **Retenção**: `settings.data.retention.usageDays` (default 30).
- **Rate limits**: regras sliding-window em `/api/rate-limits*` + limites por
  key (rpm/tpm/dailyTokens).
- **Telemetria**: `usageHistory` + `requestDetails` por request; analytics em
  `/api/usage/*` e nas páginas Analytics do dashboard.

## Notas de performance

- `HotCache` cacheia todas as leituras de config (TTL 5–60 s, invalidação
  write-through).
- `UsageWriter` serializa telemetria fora do path do request (channel de 10 k,
  fallback inline — uso nunca é perdido).
- Medido: ~251 rps / p50 25 ms em concorrência 40 contra upstream stub.
- Índices: `usageHistory(ApiKey)`, `chatSessions(KeyId,Model)`,
  `providerConnections(Provider,IsActive)`, `quotaWindows(Provider)`.

## Saúde

`GET /api/status` (público), `GET /api/health/connections` (disponibilidade
tipada por conexão), `/api/providers/{id}/usage-quota` (probe de quota upstream).
