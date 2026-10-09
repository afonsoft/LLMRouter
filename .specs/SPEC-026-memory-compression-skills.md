# SPEC-026 — Memory, context compression, skills execution

Pendências #11, #14, #20.

## Escopo
- Memory: busca com scoring (term frequency + recency) sobre kv `memory.items`; endpoint `memory.search` retorna score. Embeddings reais ficam documentados como dependência de provider (chama `POST /v1/embeddings` quando configurado, fallback substring).
- Context compression/token-saver: pipeline no gateway — quando `settings.contextCompression.enabled`, resume/trim messages acima de `maxTokens` (estratégia upstream: drop middle + summary placeholder).
- Skills: skills habilitadas (kv `skills` com enabled:true) injetam seu SKILL.md resumido no system prompt das requests de chat.

## Aceite
- Testes: memory.search rankeia; compressão reduz payload preservando system+últimas msgs; skill enabled aparece no system prompt.
