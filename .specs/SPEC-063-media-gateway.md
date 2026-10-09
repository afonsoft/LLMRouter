# SPEC-063 — Media gateway completo

Upstream /v1 surface: `audio/{speech,transcriptions,translations}`,
`images/{edits,upscale}`, `videos/generations`, `music/generations`,
`ocr`, `segment`, `moderations`, `multimodal-embeddings`, `rerank`,
`search(+analytics)`, `voices`, `text-to-speech/[voiceId]`,
`speech-to-text`, `web/{fetch,map}`, `ws`. SPEC-010 built media-providers;
the gateway only covers chat/messages/models/responses/embeddings.

## Scope
- `Core/Media/MediaRouter.cs` — media-kind → provider resolution
  (reuses SPEC-010 catalog + SPEC-024 failover), plus per-kind request
  translators where formats differ (openai↔google↔fal etc., minimal
  passthrough-first).
- Gateway endpoints (same auth+usage tracking as chat):
  `POST /v1/audio/speech|transcriptions|translations`,
  `POST /v1/images/edits|upscale`, `POST /v1/videos/generations` (+poll
  `GET /v1/videos/generations/{id}`), `POST /v1/music/generations`,
  `POST /v1/ocr|segment|moderations|multimodal-embeddings|rerank`,
  `GET/POST /v1/voices` + `/v1/text-to-speech/{voiceId}` + `/speech-to-text`,
  `POST /v1/search` (→ provider search API, logged to usage +
  /api/usage/search-analytics), `POST /v1/web/fetch|map` (firecrawl-style
  passthrough), `GET /v1/ws` (websocket relay stub → documented).
- Usage records carry `Endpoint=<media-kind>`; cost via media pricing table.
- Tests: each kind routes to a stub provider and returns its response;
  unknown kind → 404; unsupported provider kind → descriptive 422.
