# SPEC-025 — MCP: transportes stdio/SSE + módulos restantes

Pendências #6 (resto), #18.

## Escopo
- `/mcp` já serve HTTP. Adicionar: SSE transport (`GET /mcp/sse` + `POST /mcp/sse/message` no estilo jsonrpc-over-sse do upstream) e `llmrouter mcp-stdio` no CLI (stdin/stdout JSON-RPC bridge para o endpoint local).
- Módulos faltantes no McpTools: gamification (xp/badges), plugins list/toggle, search-tools, local-corpus stub documentado.
- Scopes por API key: `data.scopes` restringe tools permitidas.

## Aceite
- `tools/list` cobre módulos novos; stdio bridge faz round-trip (teste via processo filho ou handler direto); scope negado → erro JSON-RPC.
