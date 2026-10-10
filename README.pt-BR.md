# LLMRouter

> Gateway unificado de LLMs — um único endpoint para todas as providers.
> Porte em C#/.NET 10 + Blazor WebAssembly do [OmniRoute](https://github.com/diegosouzapw/OmniRoute) (v3.8.52) e do [9router](https://github.com/decolua/9router).

**English version:** [README.md](README.md)

## O que é

LLMRouter é um proxy/roteador de LLM self-hosted: você conecta providers (OpenAI, Anthropic, Gemini e ~288 outras do catálogo OmniRoute), cria combos com fallback automático e aponta qualquer cliente compatível para um único endpoint.

- **Gateway multi-formato**: `/v1/chat/completions` (OpenAI), `/v1/messages` (Claude), `/v1/responses` (Responses API), `/v1beta/*` (Gemini) — tradução automática entre formatos, campos refusal/phase/caching preservados.
- **Combos com cascata**: 15+ estratégias de ordenação (fallback, round-robin, weighted, least-used, cost-optimized, cache-optimized, reset-aware, headroom, quota-weighted…) mais as estratégias do **auto-router** upstream (`rules`, `score`, `cost`, `latency`, `sla-aware`, `lkgp`, `nadir`), estratégias de execução (fusion, pipeline, vision-adapter), pools virtuais `auto/*`, cooldowns por modelo, fallback chains nomeadas e explicabilidade de decisões.
- **Resiliência**: circuit breakers por provider, cooldowns, lockouts por modelo, janelas de quota, disponibilidade tipada (`AVAILABLE`/`QUOTA_EXHAUSTED`/`STALE_TERMINAL`/…), flag + auto-prune opt-in de steps obsoletos em combos.
- **Foco em throughput**: cache de configuração no hot path, writer de telemetria fora do request, ~3× de ganho medido (251 rps com upstream stub, p50 ≈ 25 ms).
- **Dashboard Blazor WASM**: layout fiel ao OmniRoute (mesma identidade visual, fontes, tema dark/light/system, i18n en/pt-BR/es), ~65 páginas reais.
- **SQLite embutido**: schema espelhando o upstream (`providerConnections`, `apiKeys`, `combos`, `usageHistory`, `requestDetails`…), export/import em JSON, backups.
- **Registry de 288 providers** portado 1:1 do OmniRoute (modelos, capacidades, pricing, formatos) + catálogos sincronizados ao vivo e overrides de capacidade por modelo.

## OmniRoute vs LLMRouter

| Área | OmniRoute (upstream) | LLMRouter |
|---|---|---|
| Runtime | Next.js / React / TS | .NET 10 + ASP.NET Core + Blazor WASM |
| Formatos do gateway | OpenAI, Claude, Gemini, Responses | Iguais — portados 1:1 |
| Roteamento de combos | conjunto completo de estratégias | Mesmo conjunto + auto-router portado fielmente (rules/score/cost/latency/sla/nadir) |
| Resiliência | breakers, cooldowns, janelas de quota, disponibilidade tipada, prune de steps obsoletos | Mesmas semânticas portadas |
| Sanitização de erros | `toJsonErrorPayload` (allow-list + redação de credenciais + remoção de stack/path) | `ErrorSanitizer` — mesmas regras |
| Headers customizados por conexão | sim | sim — mesmos conjuntos de nomes proibidos |
| Compressão de contexto | framework de engines + 8 engines | igual (llmlingua/omniglyph como stubs fail-open) |
| Dashboard | React SPA | Blazor WASM SPA — mesmo layout/i18n |
| Desktop/proxy/CLI | Electron, proxy MITM, auth por assinatura de CLI | **fora de escopo** (alvo docker) |
| Deploy | app Node.js | binário único ou imagem docker (multi-arch) |

Detalhe por feature: [`docs/analysis/feature-matrix.md`](docs/analysis/feature-matrix.md), [`docs/analysis/core-gap-v3.8.52.md`](docs/analysis/core-gap-v3.8.52.md), [`docs/analysis/omniroute-3.8.52-delta.md`](docs/analysis/omniroute-3.8.52-delta.md).

## Executando

```bash
dotnet run --project src/LLMRouter.Server          # dev → http://localhost:20128
```

### Binário / CLI

```bash
dotnet publish src/LLMRouter.Server -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true -o dist
./dist/LLMRouter.Server serve                       # serve o app (porta via ASPNETCORE_URLS/PORT)
./dist/LLMRouter.Server reset-password <nova-senha> # reseta a senha do admin
./dist/LLMRouter.Server version
```

### Docker

```bash
cp .env.example .env                                # ajuste as variáveis
docker compose up -d                                # http://localhost:20128, volume /data (SQLite)
docker compose up -d llmrouter-image                # imagem publicada ghcr.io/afonsoft/llmrouter
```

O SQLite fica por padrão em `~/.local/share/LLMRouter/llmrouter.db` (ou `LLMROUTER_DB_PATH`).

### Usando o gateway

```bash
# 1. No dashboard: crie uma conexão de provider e uma API key
curl -X POST http://localhost:20128/v1/chat/completions \
  -H "Authorization: Bearer sk-..." \
  -H "Content-Type: application/json" \
  -d '{"model":"openai/gpt-4o","messages":[{"role":"user","content":"Olá"}]}'
```

## CI/CD

- **CI** (`.github/workflows/ci.yml`): build + testes unitários a cada push/PR, mais smoke do Dockerfile e validação do `docker-compose.yml`; SonarCloud + GitGuardian.
- **Release** (`.github/workflows/release.yml`): tag `v*` publica binários multi-RID (linux/mac/win), GitHub Release e imagem docker em `ghcr.io/afonsoft/llmrouter:{versão,major.minor,latest}` (multi-arch amd64/arm64, usuário `app` não-root).
- **Variáveis**: `.env.example` documenta todas (porta, `LLMROUTER_DB_PATH`, clients OAuth por provider, LLM de teste).

## Estrutura

```
src/LLMRouter.Core     — engine: registry, routing, translators, pipeline do gateway
src/LLMRouter.Server   — ASP.NET Core: serve o WASM, management API, gateway /v1/*
src/LLMRouter.Client   — Blazor WebAssembly: dashboard completo
src/LLMRouter.Shared   — DTOs compartilhados
tests/LLMRouter.Tests  — xUnit + Shouldly (370+ testes)
.specs/                — roadmap de migração SPEC-001…SPEC-079 (uma branch/PR por spec)
docs/en/ docs/pt-BR/   — documentação do sistema (EN + PT-BR)
docs/analysis/         — análises do upstream OmniRoute/9router
```

Docs auxiliares: [docs/en/](docs/en/) · [docs/pt-BR/](docs/pt-BR/)

## Roadmap de migração

A migração está organizada em specs detalhadas em [`.specs/`](.specs/README.md), executadas em sequência — uma branch `devin/spec-NNN-*` + PR por spec, com ports fiéis do core upstream (gateway + rotas) em vez de reescritas.

## Testes

```bash
dotnet test
```

## Licença

MIT — ver [LICENSE](LICENSE).
