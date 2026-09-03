# ADR 0009 — Centro Analítico de uso da Iris

- Status: aceito
- Data: 2026-09-02

## Contexto

O painel operacional mostrava totais históricos e execuções recentes, mas não
permitia identificar tendências de custo, degradação de sucesso, concentração
por modelo ou comparação entre períodos. Exportar prompts ou respostas para
obter essas respostas aumentaria desnecessariamente a exposição de dados.

## Decisão

Introduzir um contrato analítico somente leitura sobre `AiRequest` e
`AiExecution`. A consulta aceita janelas de 1 a 365 dias, usa limites de dia em
UTC e carrega também o período imediatamente anterior para calcular a variação
do custo real. Todos os registros são filtrados pelo identificador da conta
antes da agregação.

Um agregador determinístico compartilhado pelas implementações PostgreSQL e em
memória produz:

- série diária contínua, incluindo dias sem atividade;
- execuções, conversas, unidades e custos estimado/real;
- taxa de sucesso e latência média das execuções finalizadas;
- custo real do período anterior e variação percentual;
- breakdown por Provider/modelo e por estado terminal.

O painel fica em `/iris/analytics`. O mesmo resultado é exposto por
`GET /api/v1/usage/analytics`, protegido pelo escopo existente `UsageRead` e
pelo rate limit da API pessoal.

## Consequências

- nenhuma nova tabela ou migration é necessária;
- prompts, respostas e instruções de sistema não são lidos nem retornados;
- falhas entram nas métricas com custo real zero e custo estimado preservado;
- períodos sem base anterior apresentam variação indefinida em vez de uma
  porcentagem enganosa;
- a consulta cresce com as execuções das duas janelas e poderá exigir
  pré-agregação quando o volume justificar essa complexidade.
