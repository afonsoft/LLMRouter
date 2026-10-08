# SPEC-010 — Media providers (tts/stt/image/video/embedding/search)

## Goal

`/dashboard/media-providers` section (visible kinds: embedding, image, video, tts, stt,
systemone, combined web fetch+search) and the media endpoints behind them.

## Upstream sources

- 9router `src/app/(dashboard)/dashboard/media-providers/**` + `src/app/api/media-providers/**`
- `open-sse/handlers/{ttsCore,sttCore,imageGenerationCore,videoCore,embeddingsCore,search/,systemoneCore}.js` + `{ttsProviders,videoProviders,imageProviders,embeddingProviders}/`
- Registry `media`/`serviceKinds`/`ttsConfig`/`sttConfig`/`embeddingConfig`/`imageConfig`/`searchViaChat` blocks (already in `providers.json`)
- OmniRoute `/dashboard/media-providers`, `/dashboard/cache/media`

## Scope

- Media provider connection cards per kind (visibility list like upstream
  `VISIBLE_MEDIA_KINDS`), add/edit modals per kind config.
- Endpoints (if not done in SPEC-003): `/v1/audio/speech`, `/v1/audio/transcriptions`,
  `/v1/audio/voices`, `/v1/images/generations`, `/v1/embeddings`,
  `/v1/videos/{generations,edits,extensions,[id]}`, `/v1/search`, `/v1/web/fetch`,
  `/v1/systemone`.
- Each media kind: provider-specific payload building per `*Config` (format field
  drives translation), same cascade fallback per kind.

## Tests

- One endpoint per kind with fake upstream; config-driven payload shapes.
