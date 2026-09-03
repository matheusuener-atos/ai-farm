# ADR 0022 — Reward Pool financiado e settlement CP → AIC

## Contexto

A Farm já executa workloads úteis, verifica evidências, mede CP e registra
propostas econômicas imutáveis. A cadeia terminava em
`AwaitingPolicyAndFunding`: não havia regra explícita de conversão, orçamento
limitante nem lançamento `Reward` no Ledger.

## Decisão

Introduzir uma política operacional versionada e desativada por padrão. Quando
habilitada, ela declara três valores inteiros em mAIC:

- taxa por 1.000 CP;
- orçamento mensal compartilhado;
- limite máximo por settlement.

O titular da proposta pode resgatar o reward calculado pela política. A
liquidação revalida, no mesmo momento, a integridade da proposta, do cenário
econômico, do manifesto, da evidência e do lifecycle do Job. O cálculo sempre
arredonda para baixo até mAIC inteiro e nunca realiza settlement parcial.

Um lock transacional por política/período serializa concorrência no Reward
Pool. O orçamento mensal é persistido e o settlement é único por proposta. A
mesma transação PostgreSQL incrementa o orçamento consumido, grava o settlement
digestado e registra no Ledger uma partida dobrada: débito no bucket `Earned`
da Wallet e crédito na conta de origem versionada do Reward Pool.

## Consequências

- repetir um resgate devolve o mesmo settlement, sem duplicar AIC;
- alterações no orçamento exigem uma nova versão de política;
- orçamento insuficiente, política desativada ou qualquer divergência de
  integridade bloqueiam integralmente a emissão;
- o settlement aparece no Centro de Auditoria e no Saldo da Farm;
- a proposta em BRL continua sendo uma fotografia econômica independente; a
  emissão em AIC decorre apenas da política operacional explícita;
- habilitar a política exige configurar conscientemente as variáveis do
  ambiente e financiar o limite mensal aprovado.
