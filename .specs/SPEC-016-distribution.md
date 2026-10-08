# SPEC-016 — CLI, Docker, distribution, CI/CD

## Goal

Ship LLMRouter like upstream ships: single command to run, Docker images, CI/CD.

## Upstream sources

- `bin/omniroute.mjs`, `bin/reset-password.mjs`, `Makefile`, `Dockerfile`,
  `docker-compose*.yml`, `.github/workflows/**`, `cli/` (9router), `start.sh`
- `scripts/dev`, electron/ (desktop — out of scope unless requested later)

## Scope

- `llmrouter` .NET tool / self-contained single-file publish (win/linux/osx):
  `llmrouter serve` (hosts app, opens browser), `llmrouter reset-password`
  (same UX as upstream bin script), env-var config (`PORT` default 20128 like
  OmniRoute? upstream dev uses :20128/9router :20127 — pick 20128 and document).
- `Dockerfile` (multi-stage: node for tailwind → dotnet publish → runtime-deps
  or chiseled), `docker-compose.yml` with volume for the SQLite file.
- GitHub Actions: `ci.yml` (build+test+coverage comment), `release.yml`
  (tag → single-file binaries + docker image to ghcr), `docker.yml` optional.
- Version endpoint parity (`GET /api/version` checks latest GH release like
  upstream npm check → adapt to releases).
- README badges + install instructions matching the product.

## Tests

- `llmrouter serve` boots + serves; docker build succeeds in CI (smoke job).
