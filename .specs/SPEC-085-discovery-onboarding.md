# SPEC-085 — Provider discovery + onboarding presets

Upstream `discovery/scan|results|verify` + 9router `comboPresets`.

## Discovery
- `POST /api/discovery/scan {providerId}` — probe provider endpoints (e.g. Ollama `http://localhost:11434`, LAN host list from settings) → persist findings to `discoveryResults`; local-only guard (reject non-loopback callers like upstream).
- `GET /api/discovery/results[/{id}]`, `POST /api/discovery/verify/{id}` — verify a finding by probing + optional import as providerConnection.
- Provider probe registry: ollama (`/api/tags`), llama.cpp (`/v1/models`), lmstudio, textgen — ports/endpoints table-driven.

## comboPresets (9router)
- Auto-seed preset combos on first boot/when connections exist: `cc/claude-*` and `cu/*` style presets mapping client-native ids (claude-sonnet-5, gpt-5.x…) to matching provider connections (`VALID_COMBO_NAME_REGEX`, preset sources cursor+claude, extra alias targets).
- `POST /api/combos/presets/seed` + auto on connection add (setting `comboPresetsAutoSeed`, default on).

## Tests (≥4)
- scan probes loopback provider and persists result; verify imports connection; preset seed creates combo aliasing client-native id; non-loopback scan rejected.
