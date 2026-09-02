# ADR 0019 — Admissão e alocação orientadas a recursos

## Status

Aceita em 2026-09-02.

## Contexto

O Compute Agent já comunicava CPU e memória observadas e permitidas pela
política local. O manifesto também carregava limites, mas o scheduler escolhia
um Agent antes de verificar se sua capacidade autorizada atendia ao workload.
Uma incompatibilidade podia, portanto, chegar tarde ao Agent e desperdiçar uma
oferta e uma tentativa da fila.

## Decisão

Cada definição do catálogo declara CPU lógica e memória mínimas, sempre menores
ou iguais aos limites máximos do sandbox. Demandas persistem a versão do
workload e uma cópia imutável desses requisitos. O dispatcher recusa o despacho
se o catálogo ativo já não corresponde ao contrato congelado.

A elegibilidade considera, nesta ordem: supressão do embedded quando existem
Agents, pausa, heartbeat, estado online, política local, versão do protocolo,
CPU, memória e ocupação. Entre Nodes elegíveis, o scheduler escolhe o menor
excedente de CPU e depois de memória; histórico e ID são desempates estáveis.
Continua existindo no máximo um Job ativo por Node nesta versão.

Uma projeção de capacidade cruza Nodes, Jobs ativos, fila e catálogo, expondo a
matriz na interface e na API. O diagnóstico usa categorias estáveis em vez de
depender de mensagens livres.

## Consequências

- ofertas incompatíveis são bloqueadas antes da criação do Job;
- Nodes maiores ficam disponíveis para workloads mais exigentes;
- contratos enfileirados resistem a mudanças acidentais do catálogo;
- operadores enxergam por que uma demanda não possui capacidade;
- CPU e memória são admission control, não prova de hardware ou benchmark;
- GPU, múltiplos Jobs simultâneos no mesmo Agent e preempção permanecem fora
  desta versão.
