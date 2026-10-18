# SPEC-127 — Agent bridge (MITM p/ CLI agents) + Traffic inspector

> Spec retroativo — implementado nos PRs #161 (agent-bridge) e #162 (traffic
> inspector, stacked), merges 2026-10-12.

Origem: `docs/analysis/feature-coverage-audit-2026-10-11.md` — port do
agent-bridge (proxy MITM para CLI agents locais) e do Traffic Inspector do
OmniRoute (`/api/tools/traffic-inspector/*`, 16 rotas).

## Work — agent bridge (PR #161)

- `Services/AgentBridge/AgentBridgeTargets.cs` — os 10 targets upstream
  verbatim (claude-code, codex, cursor, antigravity, copilot, ghe-copilot, zed,
  open-code, kiro, trae) + DEFAULT_BYPASS passthrough (bank/gov/okta/auth0).
  `RouteConnection` mantém precedência upstream: bypass > target > passthrough.
- `MitmCa.cs` — CA raiz persistida em `~/.llmrouter/mitm/ca.crt|ca.key`
  (`LLMR_MITM_DATA_DIR` override), leaf certs por host, trust-store check,
  regenerate.
- `MitmProxy.cs` — `TcpListener` com CONNECT; interceptação TLS via leaf
  por-host + `SslStream`; model mapping de `agentBridgeMappings`; injeta
  `authorization: Bearer <first active API key>` quando ausente;
  `BufferedReader` por conexão preserva bytes após o delimiter do head.
- `AgentBridgeService` + `/api/tools/agent-bridge`: start/stop/status, agents
  (+reset/detected-models/mappings/dns), state, bypass patterns, cert
  info/download/regenerate, upstream-ca, tproxy, diagnose, repair. Payload de
  state mantém chaves legadas `{server,agents}` e
  `{serverState,agentStates,bypassPatterns,mappings}` como o upstream.
- Tabelas `agentBridgeState`/`agentBridgeBypass`/`agentBridgeMappings`
  (migração EF por SPEC-109).
- Não portado (design upstream): DNS rerouting por agent (`dns` POST persiste
  flag + retorna `requiresRoot`) e o MITM child Go/node — o proxy .NET nativo
  o substitui.

## Work — traffic inspector (PR #162)

- `InterceptedRequest` — field set verbatim do upstream `types.ts`
  (id/source/agent/method/host/path/headers/sets/status `int|"in-flight"|"error"`/
  latencies/sourceModel/mappedModel/detectedKind/contextKey/annotation/
  sessionId/note/pid).
- `MaskSecrets` — scanner linear: `Bearer <token>`→`***`,
  `sk|ak|pk-*`≥16→`first6…last2`, opaco ≥40→`first4…last2`.
- `InspectorModel` — `sanitizeHeaders` (drop hop-by-hop/forwarding,
  cookies→`[REDACTED]`), `detectKind` (host/path/JSON-body/UA →
  `llm|app|unknown`), `ComputeContextKey` (sha256(systemPrompt)[:12];
  OpenAI `messages[0].system`, Anthropic `system`, Gemini `systemInstruction`),
  `StatusBucket` + filtros `Matches` (JsonElement-safe).
- `TrafficBuffer` — ring buffer `LLMR_INSPECTOR_BUFFER_SIZE`=1000, body cap
  `LLMR_INSPECTOR_MAX_BODY_KB`=1024 KiB + marker de truncamento, push/update/
  clear broadcast a subscribers, auto-fill detectedKind+contextKey.
- `InspectorHttpProxy` — listener HTTP_PROXY: CONNECT→relay + entry de
  metadata (nota de bodies opacos do upstream); plain-HTTP→forward + captura
  completa (req/resp, latencies); `EADDRINUSE`→409 `{code,port}`.
- `HarExport` — HAR 1.2, creator `LLMRouter Traffic Inspector`, valores
  mascarados, campos custom `_source/_omniRouteId/_detectedKind/_contextKey/
  _annotation/_note`, `timings` mapeados de upstream/proxy latency.
- Persistência (migração `SPEC127_TrafficInspector`): `inspector_sessions`,
  `inspector_session_requests` (chave composta session_id+seq; append =
  max+1 + count bump em txn), `inspector_custom_hosts`.
- Rotas: `requests` GET(filters)/`{id}`/`{id}/annotation`/`{id}/replay`/DELETE ·
  `ws` (snapshot + live events) · `sessions` CRUD + `{id}/requests` +
  `{id}/export.har` · `capture-modes` GET + `http-proxy`/`system-proxy`/
  `tls-intercept` POSTs (tls-intercept persiste em `settings.mitm`, lido por
  `MitmPolicy`) · `hosts` CRUD · `internal/ingest` (Bearer
  `LLMR_INSPECTOR_INTERNAL_INGEST_TOKEN`, env ≥16 ou auto-uuid,
  constant-time compare) · `export.har`.
- Wiring: `MitmProxy.SetCapture(Action<InterceptedRequest>)` — CONNECTs
  passthrough/bypass empurram entry de metadata; requests TLS-intercepted
  empurram no start (`in-flight`) e no complete (status/latencies/bodies;
  `sourceModel`→`mappedModel` quando o mapping dispara). `Program.cs` liga ao
  buffer compartilhado (push-or-update por id).
- `system-proxy` OS-level reporta unsupported (sem caminho OS-portable ainda).

## Tests

`Spec127AgentBridgeTests` (6): registry de 10 targets, glob anchoring,
precedência de rota, CA leaf chain verify + regen, auth-required, E2E real
(TcpClient → CONNECT → SslStream ao proxy, stub gateway vê model mapeado +
key injetada + counters).

`Spec127TrafficInspectorTests` (9): mask/sanitize/detectKind/contextKey,
buffer+filtros+broadcast, sessions CRUD + HAR export,
requests/annotate/replay/clear, capture-modes + http-proxy start/stop,
hosts CRUD + tls-intercept toggle.
