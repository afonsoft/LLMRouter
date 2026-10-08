# LLMRouter

> Unified LLM gateway — one endpoint for every provider.
> C#/.NET 10 + Blazor WebAssembly port of [OmniRoute](https://github.com/diegosouzapw/OmniRoute) and [9router](https://github.com/decolua/9router).

## What it is

LLMRouter is a self-hosted LLM proxy/router: connect providers (OpenAI, Anthropic, Gemini, and ~288 more from the OmniRoute catalog), build combos with automatic fallback, and point any compatible client at a single endpoint.

- **Multi-format gateway**: `/v1/chat/completions` (OpenAI), `/v1/messages` (Claude), `/v1beta/*` (Gemini), `/v1/responses` — with automatic format translation.
- **Combo cascades**: ordered fallback and round-robin with sticky-limit, automatic capability-based reordering (vision, PDF, audio, video).
- **Blazor WASM dashboard**: faithful OmniRoute layout (same visual identity, fonts, dark/light/system theme, en/pt-BR/es i18n).
- **Embedded SQLite**: schema mirroring upstream (`providerConnections`, `apiKeys`, `combos`, `usageHistory`, `requestDetails`…).
- **288-provider registry** ported 1:1 from OmniRoute (models, capabilities, pricing, formats).

## Running

```bash
dotnet run --project src/LLMRouter.Server
# dashboard at http://localhost:5000
```

## Migration roadmap

Migration is organized into detailed specs in [`.specs/`](.specs/README.md), executed sequentially — one `devin/spec-NNN-*` branch + PR per spec. Per-feature status: [`docs/analysis/feature-matrix.md`](docs/analysis/feature-matrix.md).

## Tests

```bash
dotnet test
```

## License

MIT — see [LICENSE](LICENSE).
