#!/usr/bin/env node
// SPEC-099: llmlingua compression sidecar — mirrors the upstream
// `services/llmlingua` HTTP wrapper around @atjsh/llmlingua-2
// (ONNX TinyBERT, model atjsh/llmlingua-2-js-tinybert-meetingbank
// downloaded from Hugging Face on first use).
//   GET  /health           → {ok:true}
//   POST /compress {text, rate?} → {compressed, originalTokens, compressedTokens}
// Listens on loopback 20135 only.
import { createServer } from "node:http";

const PORT = Number(process.env.LLMLINGUA_PORT || 20135);

let compressorPromise = null;
function compressor() {
  compressorPromise ??= (async () => {
    const { PromptCompressor } = await import("@atjsh/llmlingua-2");
    return new PromptCompressor({
      modelName: "atjsh/llmlingua-2-js-tinybert-meetingbank",
    });
  })();
  return compressorPromise;
}

const server = createServer(async (req, res) => {
  const send = (code, obj) => {
    res.writeHead(code, { "content-type": "application/json" });
    res.end(JSON.stringify(obj));
  };
  try {
    const url = new URL(req.url, "http://127.0.0.1");
    if (req.method === "GET" && url.pathname === "/health") return send(200, { ok: true });
    if (req.method === "POST" && url.pathname === "/compress") {
      let raw = "";
      for await (const chunk of req) raw += chunk;
      const { text, rate = 0.5 } = JSON.parse(raw || "{}");
      if (typeof text !== "string" || text.length === 0)
        return send(400, { error: "text required" });
      const c = await compressor();
      const compressed = await c.compressPrompt(text, { rate: Math.min(1, Math.max(0, rate)) });
      const originalTokens = await c.tokenLength(text).catch(() => null);
      const compressedTokens = await c.tokenLength(compressed).catch(() => null);
      return send(200, { compressed, originalTokens, compressedTokens });
    }
    return send(404, { error: "not found" });
  } catch (err) {
    res.writeHead(500, { "content-type": "application/json" });
    res.end(JSON.stringify({ error: String(err?.message || err) }));
  }
});

server.listen(PORT, "127.0.0.1", () => {
  console.log(`llmlingua sidecar listening on 127.0.0.1:${PORT}`);
});
