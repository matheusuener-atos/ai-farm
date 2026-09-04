# ADR 0036 — Farm contínua: consumo do poder autorizado sob demanda

## Status

Aceito em 2026-09-04.

## Contexto

Com o painel do Agent no ar, a ociosidade ficou visível: nodes ONLINE com
`JOBS=0` o dia inteiro. O catálogo existente prova o ciclo com jobs de ~35ms
— não consome capacidade. O dono do produto pediu um jeito de a plataforma
usar de fato o poder computacional das máquinas sempre que desejar. A
restrição estrutural é a verificação por reexecução determinística (ADR
0013): ela roda inline no submit do resultado e custa 1:1 no servidor, e o
lifecycle tem um deadline fixo de ~30s por job.

## Decisão

1. **Workload de consumo** `sha256-chain-marathon-v1` (SHA256_CHAIN,
   2.000.000 de iterações ≈ 0,6s de CPU, 20 CP, timeout 30s): pesado o
   bastante para aparecer no gráfico de desempenho, curto o bastante para a
   reexecução inline e o deadline do lifecycle.
2. **Liga/desliga por conta** (`accounts.ContinuousFarmEnabled`, padrão
   desligado) com toggle na página Meu computador — "sempre que a gente
   desejar" é literal: um clique liga, um clique desliga.
3. **Top-up no claim**: quando um Agent ocioso pede trabalho e a fila está
   vazia, o servidor cria na hora um job marathon **pinado no próprio node**
   (novo `targetNodeId` no seletor de execução, mantendo capacidade,
   confiabilidade e advisory lock por conta). O consumo segue exatamente a
   capacidade que pede trabalho — não há fila para encher nem watchdog.
4. **Ritmo pago na mesma moeda**: como o servidor reexecuta 1:1, o top-up é
   limitado por node via `AIFarm:Farm:Continuous:{CooldownSeconds=20,
   MaxJobsPerWindow=1}` — ~3% de CPU de servidor por node no padrão,
   ajustável por configuração conforme a infraestrutura crescer.
5. **Trava de versão**: o top-up só acontece para Agents no binário atual
   (bump para `atos-agent/0.9.0`); um binário antigo não conhece o workload,
   rejeitaria o próprio job e sujaria a confiabilidade do node.

## Consequências

- um node ocioso com a farm contínua ativa passa a entregar trabalho
  verificado continuamente, acumulando 20 CP por job dentro da economia
  existente (settlement do ADR 0022/0033 intacto, com teto por settlement e
  orçamento mensal protegendo o AIC);
- o teto real de consumo é o custo de verificação do servidor — dobrar o
  consumo exige dobrar verificação. Saturar máquinas de verdade continua
  dependendo de verificação por amostragem de segmentos ou quórum
  redundante (ADRs 0020/0024 como base), registrado como evolução futura;
- benchmarks agendados, demandas e o pool da plataforma continuam com
  prioridade natural: o top-up só acontece quando a fila do node está vazia.
