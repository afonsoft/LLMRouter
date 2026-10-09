# SPEC-034 — Context engines: framework, páginas context/* e APIs compression/*

## Origem
Gap-scan (`docs/analysis/upstream-gap-scan.md`, grupo 6): os 12 links `context/*` da sidebar caem em placeholder. Upstream tem um sistema de engines de compressão (`open-sse/services/compression/engines/*`, 15 engines, ~13K linhas) com pipeline empilhável, combos de compressão atribuíveis a combos de modelo, e páginas por engine. SPEC-026/033 entregaram `ContextCompressor` (drop por maxTokens) + `RtkFilters` — esta spec formaliza o framework e porta os engines viáveis.

## Escopo

### Core — `LLMRouter.Core/Compression/`
- **`ICompressionEngine`**: `Id`, `Name`, `Description`, `Icon`, `Stackable`, `StackPriority`, `Stable`, `Apply(JsonElement body, EngineOptions) -> CompressionResult`, `ConfigSchema() -> EngineConfigField[]` (boolean/number/string/select), `ValidateConfig`. `CompressionResult` = `{ Body, Stats{Engine,Before,After,SavedChars}, Skipped? }`.
- **`CompressionRegistry`**: builtins na mesma `stackPriority` do upstream — session-dedup(3), ccr(4), headroom(15), caveman(20), lite(25), aggressive(30), llmlingua(35), ultra(40), rtk(45); stubs fail-open `llmlingua` (precisa de backend de modelo) e `omniglyph` (precisa do renderer omniglyph + provider `direct`/byte-preserving) registrados com `Stable:false` e `Apply` pass-through.
- **Engines portados**:
  - `LiteEngine` — collapse whitespace/newlines no texto das mensagens (exceto system).
  - `CavemanEngine` — regras regex de filler/estrutura por intensidade (lite/full/ultra), `compressRoles`, `skipRules`/`preservePatterns`, `minMessageLength`.
  - `AggressiveEngine` — aging progressivo + truncate por `maxTokensPerMessage` + compactação de tool results antigos.
  - `UltraEngine` — pruning heurístico por score (keep-ratio configurável, nunca toca system nem último turn).
  - `SessionDedupEngine` — dedup content-addressed de suffix-line-blocks ≥ minBlockChars/minBlockLines entre turns anteriores → marker `[dedup:ref sha=XXXXXXXX]`; primeiro occurrence intacto; mapa de reversão no body.
  - `CcrEngine` — blocos ≥ minChars → `[CCR retrieve hash=<24hex> chars=<N>]` + store `ccr:{principal}:{hash}` no `kv` (cap por bytes/TTL); `Retrieve(hash, principal)`.
  - `HeadroomEngine` — compactação tabular (markdown table → forma compacta, headers dedup).
  - `RtkEngine` — wrap do `RtkFilters` existente mapeando intensidade → set de filtros.
- **`CompressionPipeline`**: resolve plano = combo-assigned pipeline → `settings.compression.stackedPipeline` → `defaultMode`; roda steps por stackPriority, grava `compressionRuns` (engine, before/after chars, model, principalId).
- **`settings.compression`**: `{ defaultMode, autoTriggerMode, comboOverrides, compressionComboId, stackedPipeline, engineConfigs{engineId→cfg}, exclusions[], mcpAccessibility, outputMode{enabled,intensity} }`.

### DB
Tabelas novas (EnsureCreated): `compressionCombos` (id, name, description, pipeline json, languagePacks json, outputMode, outputModeIntensity, isDefault, createdAt, updatedAt), `compressionComboAssignments` (id, compressionComboId, routingComboId, createdAt), `compressionRuns` (id, ts, principalId, model, engineId, beforeChars, afterChars, comboId).

### Endpoints
- `/api/compression/engines` GET (catálogo + configSchema + stable)
- `/api/compression/preview` POST `{engineId|pipeline|mode, text|body, config}` → body/resultado + stats
- `/api/compression/compare` POST — mesmo input em ≥2 engines → stats lado a lado
- `/api/compression/retrieve` POST `{hash, callerId}` → bloco CCR
- `/api/compression/rules` GET, `/api/compression/language-packs` GET
- `/api/settings/compression` GET/PUT; `/api/settings/compression/mcp-accessibility` GET/PUT; `/api/settings/compression/run-telemetry` GET
- `/api/context/combos` GET/POST, `/api/context/combos/{id}` GET/PUT/DELETE, `/api/context/combos/{id}/assignments` GET/PUT
- `/api/context/analytics` GET (`?since=Nd`), `/api/context/analytics/engine` GET (`?engineId&days`)
- `/api/context/caveman/config` GET/PUT

### Gateway
`GatewayEndpoints`: onde hoje aplica `ContextCompressor`/`RtkFilters` passa a resolver o plano via `CompressionPipeline` (assignment do combo do model > stackedPipeline > defaultMode > tokenSaver legado) e grava `compressionRuns`.

### Client — `LLMRouter.Client/Pages/`
- `EngineConfigPage.razor` (param `EngineId`): catálogo → form schema-driven de config → save em `settings.compression.engineConfigs[id]`; caixa de preview (texto → `/api/compression/preview`, mostra before/after + savings); tile de analytics 7d.
- 7 páginas wrapper (aggressive, ccr, headroom, lite, llmlingua, session-dedup, ultra) + `ContextCavemanPage.razor`, `ContextCombosPage.razor` (CRUD pipeline + assign a routing combos), `ContextSettingsPage.razor` (defaultMode/autoTrigger/exclusions/comboOverrides/mcp-accessibility + run-telemetry), `ContextOmniglyphPage.razor` (enable + preview, marcado experimental). `context/rtk` já resolve TokenSaver (SPEC-033+PR40).
- i18n en/pt-BR/es para os labels novos.

## Aceite
- `CompressionEngineTests` (engines individuais, pipeline order, CCR roundtrip, session-dedup marker) + `CompressionEndpointsTests` (catálogo, preview, combos CRUD+assignments, settings roundtrip, analytics) verdes; suíte inteira verde; CI verde.
- Os 12 links `context/*` da sidebar abrem páginas reais (sem "coming soon").
