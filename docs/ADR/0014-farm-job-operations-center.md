# ADR 0014 — Centro operacional de Jobs da Farm

## Status

Aceito em 2026-09-02.

## Contexto

O Compute Lab apresentava somente os dez Jobs mais recentes. A API permitia
consultar um Job conhecido, mas não descobrir execuções antigas, separar origem
interativa de automação nem exportar um recorte auditável. Isso dificultava
operação, suporte e investigação de integridade à medida que o histórico crescia.

## Decisão

- Criar `/farm/jobs` como Centro de Jobs autenticado e isolado por conta.
- Permitir filtros por busca, workload catalogado, status, origem, período UTC
  e somente falhas de integridade.
- Paginar de 1 a 100 itens e limitar a varredura a 5.000 registros, sinalizando
  explicitamente quando o resultado for truncado.
- Reutilizar a projeção e a verificação profunda do Compute Lab, incluindo
  manifesto, bundle e lifecycle, sem implementar uma segunda regra de validade.
- Classificar a origem como `Interactive` ou `PersonalApi` sem expor o ID do
  token nem sua chave de idempotência.
- Expor consulta e exportação pela API pessoal exclusivamente sob `FarmRead`.
- Exportar JSON em envelope versionado com SHA-256 do payload canônico.

## Consequências

Operadores e integrações podem descobrir e investigar todo o histórico recente
sem conhecer previamente o ID de cada Job. Totais de conclusão e CP consideram
somente evidências profundamente válidas. O limite de varredura impede respostas
sem fronteira; contas maiores devem restringir filtros ou período. O recurso não
altera Jobs, não concede `FarmExecute`, não converte CP em AIC e não exige nova
migration.
