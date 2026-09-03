# ADR 0024 — Retentativa e failover persistentes por shard

## Status

Aceita em 2026-09-03.

## Contexto

Demandas distribuídas preservavam o fan-out e a consolidação verificável, mas
uma falha terminal em qualquer shard encerrava toda a demanda. Uma interrupção
transitória de máquina desperdiçava os shards já aprovados e exigia uma nova
submissão completa, mesmo quando outro Node possuía capacidade.

## Decisão

Cada demanda congela entre uma e cinco tentativas permitidas por shard, usando
três por padrão. Cada Job originado pela fila recebe um número de tentativa e a
unicidade passa a ser `(demanda, shard, tentativa)`. O shard referencia somente
o Job corrente ou vencedor; Jobs anteriores continuam imutáveis e auditáveis.

Falha, timeout, rejeição ou cancelamento terminal agendam nova tentativa com
backoff exponencial limitado a cinco minutos. O próximo despacho evita o Node
da tentativa imediatamente anterior, exigindo failover real quando outra
máquina elegível existe. Se não houver alternativa, a demanda permanece em
processamento e volta a tentar até sua validade. Ao esgotar o orçamento, ela
termina como falha sem descartar evidências já produzidas.

Restart entre criação e vínculo recupera apenas Jobs ativos ou concluídos. Uma
tentativa terminal já liberada para retry não é religada acidentalmente. A
consolidação e o download verificam exclusivamente o Job vencedor de cada
shard, enquanto a API e a interface apresentam todo o histórico.

## Consequências

- falhas transitórias de um Node não condenam imediatamente a demanda;
- shards aprovados não são reexecutados;
- o histórico prova quais Nodes tentaram cada parte e por que falharam;
- idempotência suporta múltiplas tentativas sem duplicar a mesma tentativa;
- o número máximo é parte persistida do contrato da demanda;
- failover exige outro Node elegível; não há execução arbitrária fora do
  catálogo nem relaxamento de CPU, memória, rede ou classificação de dados.
