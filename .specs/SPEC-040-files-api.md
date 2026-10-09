# SPEC-040 — Files API + batch files

Upstream: `/v1/files`, `/v1/files/{id}(/content)`, `/api/files*`,
`batch/files` page. `/batches` exists but can't ingest files.

## Scope
- `files` table: {id, filename, bytes, purpose, mime, createdAt}.
- `POST /v1/files` (multipart), `GET /v1/files`, `GET /v1/files/{id}`,
  `GET /v1/files/{id}/content`, `DELETE /v1/files/{id}` — OpenAI-compatible
  responses. Stored on disk under `{DbDir}/files/` + row.
- Batch integration: `POST /v1/batches` accepts `input_file_id`; batch runner
  reads jsonl from the file; `output_file_id`/`error_file_id` written back as
  files rows.
- UI: `/dashboard/batch/files` page — list, upload, download, delete
  (replaces placeholder).
- Tests: upload→list→content→delete round-trip; batch consumes input file.

## Non-goals
- Multipart streaming > 50MB (cap at 50MB, configurable).
