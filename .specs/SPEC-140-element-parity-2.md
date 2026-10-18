# SPEC-140 — Element parity round 2: input/select/icon-btn tokens

> Spec retroativo — implementado no PR #178, merge 2026-10-13.
> Follow-up e2e: PR #182 (merge 2026-10-18) inverteu a assertion de
> inset-shadow que quebrava todo PR desde SPEC-140.

Origem: segunda passada de element-fidelity — o audit de elementos (computed
styles, 16 páginas vs OmniRoute prod) mostrou quatro deltas sistemáticos, todos
em CSS global.

## Work

- **Elementos sem classe**: um `<select>` em Combos + o input de busca da
  sidebar resolviam para `Arial` do UA — adicionados `font-family: inherit` /
  `class="input"`. Sweep passa a contar 0 selects sem classe.
- **Tokens `.input`**: upstream usa `bg #f9f9fb` (`var(--bg)`), `radius 8`,
  inputs `h38`, selects compactos `h~26` (`padding:4px 8px`), **sem** inner
  shadow — o nosso tinha bg branco / radius 9 / h34 / `inset 0 2px 4px`.
  Alinhado; textarea inalterada.
- **`.icon-btn` radius**: 9 → 6px (upstream).
- Busca da sidebar: padding 7px → 6px vertical, inset shadow removido,
  `font-family: inherit` (estava `Arial` do UA).

Os deltas de audit pós-fix colapsam para ruído de conteúdo (data/state), não
tokens. Sem mudança de comportamento — CSS/markup puro.

## Tests / verification

- Element audit re-run: 0 unclassed selects, tokens `.input`/`.icon-btn`
  batendo computed styles do upstream.
- E2E (ajustado no PR #182): a guarda de `boxShadow` agora **falha se um inset
  shadow reaparecer** (documentando a decisão de paridade), em vez de exigir
  `/inset/` como antes — o check de font-size permanece.
