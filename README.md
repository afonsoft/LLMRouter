# LLMRouter

> Gateway unificado de LLMs — um único endpoint para todas as providers.
> Porte em C#/.NET 10 + Blazor WebAssembly dos projetos [OmniRoute](https://github.com/diegosouzapw/OmniRoute) e [9router](https://github.com/decolua/9router).

**English version:** [README.en.md](README.en.md)

## O que é

LLMRouter é um proxy/roteador de LLM self-hosted: você conecta providers (OpenAI, Anthropic, Gemini, e ~288 outras do catálogo OmniRoute), cria combos com fallback automático, e aponta qualquer cliente compatível para um único endpoint.

- **Gateway multi-formato**: `/v1/chat/completions` (OpenAI), `/v1/messages` (Claude), `/v1beta/*` (Gemini), `/v1/responses` — com tradução automática entre formatos.
- **Combos com cascata**: fallback ordenado e round-robin com sticky-limit, reordenação automática por capacidades (visão, PDF, áudio, vídeo).
- **Dashboard Blazor WASM**: layout fiel ao OmniRoute (mesma identidade visual, fontes, tema dark/light/system, i18n en/pt-BR/es).
- **SQLite embutido**: schema espelhando o upstream (`providerConnections`, `apiKeys`, `combos`, `usageHistory`, `requestDetails`…).
- **Registry de 288 providers** portado 1:1 do OmniRoute (modelos, capacidades, pricing, formatos).

## Executando

```bash
dotnet run --project src/LLMRouter.Server
# dashboard em http://localhost:5000
```

O banco SQLite é criado em `%LOCALAPPDATA%/LLMRouter/llmrouter.db` (ou `LLMROUTER_DB_PATH`).

### Uso do gateway

```bash
# 1. No dashboard: crie uma conexão de provider e uma API key
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Authorization: Bearer sk-..." \
  -H "Content-Type: application/json" \
  -d '{"model":"openai/gpt-4o","messages":[{"role":"user","content":"Olá"}]}'
```

## Estrutura

```
src/LLMRouter.Core     — engine: registry, routing, translators, gateway pipeline
src/LLMRouter.Server   — ASP.NET Core: serve o WASM, management API, gateway /v1/*
src/LLMRouter.Client   — Blazor WebAssembly: dashboard completo
src/LLMRouter.Shared   — DTOs compartilhados
tests/LLMRouter.Tests  — xUnit + Shouldly
.specs/                — roadmap SPEC-001…SPEC-016 (uma branch/PR por spec)
docs/analysis/         — análise upstream OmniRoute/9router
```

## Roadmap de migração

A migração está organizada em specs detalhadas em [`.specs/`](.specs/README.md),
executadas em sequência — cada spec é uma branch `devin/spec-NNN-*` e um PR.
Status por feature em [`docs/analysis/feature-matrix.md`](docs/analysis/feature-matrix.md).

## Testes

```bash
dotnet test
```

## Licença

MIT — ver [LICENSE](LICENSE).
