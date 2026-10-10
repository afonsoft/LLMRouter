# LLMRouter

> Unified LLM gateway — one endpoint for every provider.
> C#/.NET 10 + Blazor WebAssembly port of [OmniRoute](https://github.com/diegosouzapw/OmniRoute) (v3.8.52) and [9router](https://github.com/decolua/9router).

**Versão em português:** [README.pt-BR.md](README.pt-BR.md)

## What it is

LLMRouter is a self-hosted LLM proxy/router: you connect providers (OpenAI, Anthropic, Gemini, and ~288 more from the OmniRoute catalog), build combos with automatic fallback, and point any compatible client at a single endpoint.

- **Multi-format gateway**: `/v1/chat/completions` (OpenAI), `/v1/messages` (Claude), `/v1/responses` (Responses API), `/v1beta/*` (Gemini) — automatic translation between formats, refusal/phase/caching fields preserved.
- **Combos with cascades**: 15+ ordering strategies (fallback, round-robin, weighted, least-used, cost-optimized, cache-optimized, reset-aware, headroom, quota-weighted…) plus the upstream **auto-router** strategies (`rules`, `score`, `cost`, `latency`, `sla-aware`, `lkgp`, `nadir`), execution strategies (fusion, pipeline, vision-adapter), virtual `auto/*` pools, model cooldowns, named fallback chains and routing decisions explainability.
- **Resilience**: provider circuit breakers, cooldowns, model-scoped lockouts, quota windows, typed availability (`AVAILABLE`/`QUOTA_EXHAUSTED`/`STALE_TERMINAL`/…), stale combo-step flagging + opt-in auto-prune.
- **Throughput-oriented**: hot-path config caching, off-request telemetry writer, ~3× measured throughput gain over the naive path (251 rps on a stub upstream, p50 ≈ 25 ms).
- **Blazor WASM dashboard**: faithful OmniRoute layout (same visual identity, fonts, dark/light/system theme, en/pt-BR/es i18n), ~65 real pages.
- **Embedded SQLite**: schema mirroring upstream (`providerConnections`, `apiKeys`, `combos`, `usageHistory`, `requestDetails`…), JSON export/import, backups.
- **288-provider registry** ported 1:1 from OmniRoute (models, capabilities, pricing, formats) + synced live model catalogs and per-model capability overrides.

## OmniRoute vs LLMRouter

| Area | OmniRoute (upstream) | LLMRouter |
|---|---|---|
| Runtime | Next.js / React / TS | .NET 10 + ASP.NET Core + Blazor WASM |
| Gateway formats | OpenAI, Claude, Gemini, Responses | Same — ported 1:1 |
| Combo routing | full strategy set | Same set + auto-router ported faithfully (rules/score/cost/latency/sla/nadir) |
| Resilience | breakers, cooldowns, quota windows, typed availability, stale-step prune | Same semantics ported |
| Error sanitization | `toJsonErrorPayload` (field allow-list + credential redaction + stack/path strip) | `ErrorSanitizer` — same rules |
| Custom per-connection headers | yes | yes — same forbidden-name sets |
| Context compression | engine framework + 8 engines | same (llmlingua/omniglyph as fail-open stubs) |
| Dashboard | React SPA | Blazor WASM SPA — same layout/i18n |
| Desktop/proxy/CLI | Electron, MITM proxy, CLI subscription auth | **out of scope** (docker target) |
| Deploy | Node.js app | single binary or docker image (multi-arch) |

Feature-level detail: [`docs/analysis/feature-matrix.md`](docs/analysis/feature-matrix.md), [`docs/analysis/core-gap-v3.8.52.md`](docs/analysis/core-gap-v3.8.52.md), [`docs/analysis/omniroute-3.8.52-delta.md`](docs/analysis/omniroute-3.8.52-delta.md).

## Running

```bash
dotnet run --project src/LLMRouter.Server          # dev → http://localhost:20128
```

### Binary / CLI

```bash
dotnet publish src/LLMRouter.Server -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true -o dist
./dist/LLMRouter.Server serve                       # serve app (port via ASPNETCORE_URLS/PORT)
./dist/LLMRouter.Server reset-password <new-pass>   # reset admin password
./dist/LLMRouter.Server version
```

### Docker

```bash
cp .env.example .env                                # adjust variables
docker compose up -d                                # http://localhost:20128, /data volume (SQLite)
docker compose up -d llmrouter-image                # published ghcr.io/afonsoft/llmrouter image
```

SQLite DB defaults to `~/.local/share/LLMRouter/llmrouter.db` (or `LLMROUTER_DB_PATH`).

### Using the gateway

```bash
# 1. In the dashboard: create a provider connection and an API key
curl -X POST http://localhost:20128/v1/chat/completions \
  -H "Authorization: Bearer sk-..." \
  -H "Content-Type: application/json" \
  -d '{"model":"openai/gpt-4o","messages":[{"role":"user","content":"Hello"}]}'
```

## CI/CD

- **CI** (`.github/workflows/ci.yml`): build + unit tests on every push/PR, plus Dockerfile smoke and `docker-compose.yml` validation; SonarCloud + GitGuardian.
- **Release** (`.github/workflows/release.yml`): tag `v*` publishes multi-RID binaries (linux/mac/win), a GitHub Release, and the docker image at `ghcr.io/afonsoft/llmrouter:{version,major.minor,latest}` (multi-arch amd64/arm64, non-root `app` user).
- **Variables**: `.env.example` documents all of them (port, `LLMROUTER_DB_PATH`, per-provider OAuth clients, test LLM).

## Structure

```
src/LLMRouter.Core     — engine: registry, routing, translators, gateway pipeline
src/LLMRouter.Server   — ASP.NET Core: serves the WASM, management API, gateway /v1/*
src/LLMRouter.Client   — Blazor WebAssembly: full dashboard
src/LLMRouter.Shared   — shared DTOs
tests/LLMRouter.Tests  — xUnit + Shouldly (370+ tests)
.specs/                — migration roadmap SPEC-001…SPEC-079 (one branch/PR per spec)
docs/en/ docs/pt-BR/   — system documentation (EN + PT-BR)
docs/analysis/         — upstream OmniRoute/9router analysis
```

Auxiliary docs: [docs/en/](docs/en/) · [docs/pt-BR/](docs/pt-BR/)

## Migration roadmap

Migration is organized into detailed specs in [`.specs/`](.specs/README.md), executed sequentially — one `devin/spec-NNN-*` branch + PR per spec, faithful ports of the upstream core (gateway + routing) rather than rewrites.

## Tests

```bash
dotnet test
```

## License

MIT — see [LICENSE](LICENSE).
