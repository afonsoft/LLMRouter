// Upstream stub for perf tests — answers /v1/models and /v1/chat/completions
// with OpenAI-shaped payloads. Deliberately no keep-alive tricks: plain node
// http server is stable under concurrency (python http.server is not).
import http from "node:http";

const MODELS = { object: "list", data: [
  { id: "stub-a", object: "model" }, { id: "stub-b", object: "model" },
  { id: "stub-coding", object: "model" }, { id: "stub-mini", object: "model" },
]};

const srv = http.createServer((req, res) => {
  const chunks = [];
  req.on("data", (c) => chunks.push(c));
  req.on("end", () => {
    if (req.method === "GET" && req.url.startsWith("/v1/models")) {
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify(MODELS));
      return;
    }
    if (req.method === "POST" && req.url.startsWith("/v1/chat/completions")) {
      const body = JSON.parse(Buffer.concat(chunks).toString() || "{}");
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify({
        id: "chatcmpl-stub", object: "chat.completion",
        model: body.model ?? "stub-a",
        choices: [{ index: 0, message: { role: "assistant", content: "pong" }, finish_reason: "stop" }],
        usage: { prompt_tokens: 4, completion_tokens: 2, total_tokens: 6 },
      }));
      return;
    }
    res.writeHead(404); res.end();
  });
});
srv.listen(Number(process.env.PORT ?? 19001), "127.0.0.1");
