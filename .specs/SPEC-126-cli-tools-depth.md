# SPEC-126 — CLI tools depth: registry, settings RW, apply, backups

> Spec retroativo — implementado no PR #160, merge 2026-10-12.

Origem: port da superfície `/api/cli-tools/*` do OmniRoute (`cliRuntime.ts`).
As páginas CLI-tools do dashboard ganham backend real — detecção de tools,
settings read/write, apply e backups — em vez do surface fino de cli-agents.

## Work

- `CliToolService`: registry de 27 tools portado verbatim do upstream
  (`CLI_TOOLS`): `claude`, `codex`, `droid`, `openclaw`, `cursor`, `windsurf`,
  `devin`, `zcode`, `cline`, `kilo`, `continue`, `opencode`,
  `hermes`/`hermes-agent`, `qoder`, `qwen`, `aider`, `goose`, `gemini`,
  `forge`, `jcode`, `omp`, `letta`, `codewhale`, `smelt`, `pi`, `crush` —
  cada um com `defaultCommand`, `envBinKey` (ex. `CLI_CLAUDE_BIN`), timeout de
  healthcheck, config path e template de env-var.
- Endpoints: `all-statuses`, `status`, `detect`, `runtime`, `config`, `keys`,
  `logs`, `backups` + `backups/restore`, `apply` (com `dryRun`) e
  `{tool}-settings` GET/POST — o padrão de rota por tool do upstream
  (`/api/cli-tools/claude-settings` etc.).
- Semântica de escrita fiel ao upstream: JSON faz merge das vars no objeto
  `env` top-level; TOML/YAML/texto recebem bloco idempotente
  `# >>> llmrouter … # <<<`; toda escrita faz snapshot em
  `<config-dir>/.llmrouter.bak/`; `keyId` resolve a chave real do DB para que
  valores mascarados nunca cheguem ao disco (upstream #523/#526).
- Guards: `LLMR_ALLOW_CONFIG_WRITES=false` desabilita escritas (403);
  `LLMR_CONFIG_HOME` valida absolute + dentro de home + sem traversal antes de
  sobrescrever a raiz de config.

## Tests

`Spec126CliToolsTests` (7): registry cobre todos os tools upstream,
`binary_not_found`, env merge preserva chaves não tocadas + cria backup, bloco
TOML managed substitui idempotentemente, semântica "configured" do upstream
(presença da env-var), `dryRun` não escreve nada, endpoints exigem auth.
