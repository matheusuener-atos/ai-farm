# ADR 0041 — Biblioteca unificada e o "auto" como orquestrador

## Status

Aceito em 2026-09-05.

## Contexto

O mesmo ativo — o conhecimento indexado da conta — tinha três portas
(Demandas úteis, Conhecimento, memória do chat) e nenhum caminho de
alimentação natural. Com a rota AI Farm Network (ADR 0040), a biblioteca
virou o coração do produto e a fragmentação ficou insustentável.

## Decisão

- **Página Biblioteca** (`/biblioteca`, com a rota antiga `/farm/knowledge`
  preservada): adicionar conteúdo (vira demanda de indexação executada pela
  rede, shards por tamanho), acompanhar o que está indexado e buscar — tudo
  num lugar, presente também na navegação Lite.
- **O chat alimenta a biblioteca**: ação "Guardar na Biblioteca" por
  resposta — o conteúdo vira demanda de indexação com o título da conversa.
  Gesto explícito, sem indexação silenciosa de conversas.
- **O "auto" orquestra as rotas**: com preferência automática, o seletor
  sonda a biblioteca; cobertura boa (score ≥ 3) envia para a rede (AI Farm
  Network), senão segue para o provider externo. Preferência explícita nunca
  é sobrescrita e o preflight sempre mostra a rota escolhida. A sonda nunca
  derruba o chat: qualquer falha cai no externo.
- **Coerência**: textos pré-settlement corrigidos ("sem emissão de AIC" →
  resgate real), fluxo de vínculo da página Farm apontando para o handshake
  dos assentos, benchmark reposicionado como diagnóstico ("Testar esta
  máquina") e agendamentos como manutenção.

## Consequências

- o ciclo completo fica autoalimentado: conversar → guardar → a rede indexa
  → o auto passa a responder da rede → CP para quem computou;
- a Lite ganha a Biblioteca sem jargão técnico; Demandas úteis permanece
  como console técnico no modo completo;
- o limiar do auto é uma constante documentada (`AutoRouteScoreThreshold`),
  ajustável com dados reais de uso.
