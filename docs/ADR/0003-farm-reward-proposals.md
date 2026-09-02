# ADR 0003 — Propostas de recompensa antes da liquidação

- Status: aceito; liquidação continua bloqueada
- Data: 2026-09-02

## Contexto

A Farm já produz evidência técnica assinada e fotografias econômicas
imutáveis. O Ledger possui o tipo `Reward`, mas ainda não existe fonte legítima
para o Reward Pool, orçamento aprovado ou regra CP → AIC.

## Decisão

Introduzir uma proposta de recompensa append-only como fronteira entre medição
e pagamento. A proposta somente é criada quando:

- o Job pertence à conta, está concluído e sua evidência HMAC é válida;
- o cenário pertence à mesma conta e seu digest é válido;
- o cenário referencia exatamente o Job e o digest da evidência atual.

O registro congela Job, cenário, CP, reward recomendado em BRL, digests das
fontes, versão, horário, estado e digest SHA-256 próprio. Uma constraint única
por conta e cenário torna a criação idempotente.

## Consequências

O estado inicial e único é `AwaitingPolicyAndFunding`. Não há lançamento no
Ledger, geração de `Earned` ou conversão CP → AIC. O valor em BRL é uma
recomendação derivada do cenário e não constitui preço, promessa ou saldo.

Uma futura liquidação deverá exigir, no mínimo, Reward Pool financiado,
orçamento disponível, autoridade de aprovação, política versionada e uma nova
transação balanceada no Ledger. Esses requisitos não podem ser inferidos desta
proposta.
