# SPEC-069: Combo ops — test/reorder/defaults

Core-gateway gap vs OmniRoute 3.8.52 (docker deployment focus — proxy/CLI/desktop out of scope).

## Scope
POST /api/combos/{id}/test faz probe do candidate pool; POST /api/combos/reorder persiste ordem; GET/PUT /api/settings/combo-defaults (default combo, handoffModel). UI: ordenação+test no Combos.

## Conventions (repo)
- Entities/DbSets/DDL já scaffolded em main; stub endpoints em src/LLMRouter.Server/Endpoints/.
- Endpoint recipe: `public static class XxxEndpoints { public static void Map(WebApplication app) { var g = app.MapGroup("/api").RequireAuthorization(); ... } }`.
- POST 4xx MUST return JSON body (StatusCodePages re-executes → 405 sem body).
- combo.Models é JSON List<string> ["openai/gpt-4o"].
- JsonNode: `model = x; node["model"] = model;` (não `model = node["model"] = x`).
- Razor: extrair `var x = ...` antes de atributos com quotes aninhados.
- Tests: xUnit+Shouldly+WebApplicationFactory, Db:Path temp único, login /api/auth/login {password:"test1234"}.
