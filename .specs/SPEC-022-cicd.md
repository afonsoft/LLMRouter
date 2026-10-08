# SPEC-022 — CI/CD completo (testes, compose, .env, deploy por tag)

> Objetivo: pipeline de CI/CD no GitHub Actions cobrindo testes unitários,
> validação do Docker/compose, `.env` para variáveis e deploy da imagem
> Docker (GHCR) ao criar uma tag, mais melhorias de robustez.

## Escopo
- [x] `.env.example` com todas as variáveis (server, DB, OAuth, LLM test) + `.env` gitignored
- [x] `docker-compose.yml`: `env_file: .env`, serviço `llmrouter` (build local) + `llmrouter-image` (profile `published`, imagem GHCR)
- [x] CI (`ci.yml`): build + `dotnet test` (Release, TRX artifact) + job `docker` que faz `docker build` (smoke, sem push) + `docker compose config` pra validar o compose
- [x] Release (`release.yml`) ao push de tag `v*`: binários multi-RID, GitHub Release, docker build+push GHCR multi-arch (amd64+arm64) com tags semver via `docker/metadata-action`
- [x] README: seção CI/CD + variáveis + uso do compose

## Critérios de aceite
- PR passa CI (build-test + docker jobs verdes)
- `docker compose config` válido com .env.example copiado
- Tag `v*` dispara release.yml com push da imagem para ghcr.io/afonsoft/llmrouter:{version,latest}
