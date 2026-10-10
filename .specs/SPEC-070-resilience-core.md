# SPEC-070: Resilience — model cooldowns + fallback chains

Core-gateway gap vs OmniRoute 3.8.52 (docker deployment focus — proxy/CLI/desktop out of scope).

## Scope
routing/model-cooldowns (cooldown por provider+model, integra no ComboStrategies); /api/fallback/chains CRUD; /api/routing/decisions/{requestId} trace explicável. UI: seção em Resilience.

## Conventions (repo)
- Entities/DbSets/DDL já scaffolded em main; stub endpoints em src/LLMRouter.Server/Endpoints/.
- Endpoint recipe: `public static class XxxEndpoints { public static void Map(WebApplication app) { var g = app.MapGroup("/api").RequireAuthorization(); ... } }`.
- POST 4xx MUST return JSON body (StatusCodePages re-executes → 405 sem body).
- combo.Models é JSON List<string> ["openai/gpt-4o"].
- JsonNode: `model = x; node["model"] = model;` (não `model = node["model"] = x`).
- Razor: extrair `var x = ...` antes de atributos com quotes aninhados.
- Tests: xUnit+Shouldly+WebApplicationFactory, Db:Path temp único, login /api/auth/login {password:"test1234"}.
