# ADR 0010 — Centro Analítico da Farm

- Status: aceito
- Data: 2026-09-02

## Contexto

O Compute Lab, o Agent e o simulador econômico já preservavam Jobs,
telemetria, evidências, cenários e propostas por conta. Essas informações,
porém, estavam distribuídas em telas operacionais e não ofereciam uma visão de
tendência, produtividade ou confiabilidade da Farm.

## Decisão

Introduzir uma consulta analítica somente leitura e filtrada pela conta. Ela
carrega a janela solicitada e o período anterior, agrega os dados em memória e
produz:

- Jobs ativos, concluídos e falhos, com taxa de sucesso;
- CP verificado e vazão em CP por hora somente para Jobs concluídos;
- duração média, pico de memória e tempo de CPU observado;
- série diária contínua e breakdown por workload e estado;
- estado efetivo do Node/Agent, considerando expiração do heartbeat;
- totais do pipeline econômico ainda não liquidado.

O painel autenticado fica em `/farm/analytics`. O contrato também é exposto por
`GET /api/v1/farm/analytics`, com janela de 1 a 365 dias, rate limit pessoal e
o novo escopo mínimo `FarmRead`.

## Consequências

- a consulta nunca agenda, despacha, cancela ou executa Jobs;
- CP continua sendo medição técnica e não estabelece conversão para AIC;
- cenários e propostas são informativos e nenhum pagamento é realizado;
- contas não compartilham Jobs, Nodes ou dados econômicos;
- o novo bit de escopo exige ampliar de 63 para 127 o check constraint de
  `api_tokens`;
- o custo da consulta cresce com duas janelas e poderá ser substituído por
  pré-agregações quando o volume justificar.
