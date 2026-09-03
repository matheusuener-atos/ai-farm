# ADR 0023 — Confiabilidade de Nodes e quarentena automática

## Status

Aceita em 2026-09-03.

## Contexto

O scheduler conhecia disponibilidade, compatibilidade e capacidade residual,
mas tratava da mesma forma um Node estável e outro que acabara de falhar várias
vezes. Isso permitia retentativas repetidas em máquinas degradadas, aumentava o
tempo das demandas e exigia intervenção manual para pausar o Node.

## Decisão

A confiabilidade é calculada deterministicamente a partir dos resultados mais
recentes de cada Node. O score usa uma janela limitada e um prior conservador,
evitando classificar uma máquina como confiável com amostra insuficiente.
Conclusões contam como sucesso; falhas, timeouts e rejeições contam como falha;
cancelamentos não penalizam o Node.

Três falhas consecutivas, por padrão, abrem quarentena automática de 30 minutos.
Durante esse período o Node continua visível e online, mas não recebe novos
Jobs. A quarentena expira sem mutação persistente e uma execução aprovada zera a
sequência de falhas. Entre Nodes elegíveis, o scheduler prefere o maior score de
confiabilidade antes de aplicar o best-fit de CPU e memória.

A projeção carrega, em uma única consulta PostgreSQL, no máximo a janela
configurada de cada Node. A matriz de capacidade e a API exibem score, faixa,
amostra, sequência e prazo da quarentena. O centro de incidentes abre um alerta
crítico enquanto a quarentena estiver ativa.

## Consequências

- Nodes degradados deixam de consumir novas demandas durante o período de
  recuperação;
- Nodes com histórico melhor recebem preferência sem ignorar capacidade;
- o cálculo é reproduzível e não exige coluna, tabela ou migration nova;
- o histórico completo de Jobs permanece imutável e auditável;
- quarentena não encerra Jobs já ativos nem substitui pausa manual;
- parâmetros podem ser ajustados por ambiente, mas defaults iguais preservam o
  mesmo comportamento em desenvolvimento e produção.
