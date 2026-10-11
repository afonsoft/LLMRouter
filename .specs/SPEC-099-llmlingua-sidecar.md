# SPEC-099 — llmlingua sidecar real backend (ServiceManager + engine dispatch)

Upstream `12967-llmlingua-server`: embedded-service supervisor + HTTP sidecar
wrapping the real `@atjsh/llmlingua-2` package (ONNX TinyBERT, model
`atjsh/llmlingua-2-js-tinybert-meetingbank` downloaded from Hugging Face on
first use) on loopback port **20135**. Engine dispatches `POST /compress`;
when the sidecar is down the engine fail-opens (pass-through) — worst case is
no savings, never corruption.

Our `llmlingua` engine is registered-for-parity only (fail-open pass-through);
ServiceManager catalog has the placeholder entry (wrong port 8899).

## Work

- `scripts/llmlingua-sidecar.mjs` committed in-repo (like `scripts/perf/stub.mjs`):
  imports `@atjsh/llmlingua-2`, serves `GET /health` → `{ok:true}` and
  `POST /compress {text, rate?}` → `{compressed, originalTokens, compressedTokens}`.
- `ServiceManager.Descriptor`: add `NpmPackage` (name/version) + `EntryScript`
  fields; install = `npm install <pkg>` into `services/llmlingua/` + copy the
  committed script; spawn = `node <script>`; port → 20135, healthPath `/health`.
  Keep adopted-process detection working.
- `LlmLinguaEngine.Apply` (Core/Compression/Engines.cs): when ServiceManager
  reports the service running → POST `http://127.0.0.1:20135/compress`
  (`{text, rate}` where rate = target ratio from step cfg), timeout ~30s,
  parse `compressed`; ANY error (down, timeout, bad body) → current fail-open
  pass-through unchanged.
- Do NOT auto-install at boot — sidecar opt-in via `/api/services/llmlingua/…`
  like every other service (docker image doesn't ship npm by default).
- `omniglyph`: stays a declared stub — needs byte-preserving image transport
  end-to-end; document in the spec/code comment that only the catalog entry is
  shipped (upstream labels it experimental too).

## Tests (≥3)

- Descriptor: llmlingua carries npm package + port 20135 + health path;
- engine Apply fail-opens when the service is stopped (existing behavior holds);
- engine Apply calls the sidecar when `ServiceManager` reports running — use a
  test HttpMessageHandler (engine must accept an injected HttpClient/handler);
- sidecar script exists in repo and parses as valid JS (node --check).
