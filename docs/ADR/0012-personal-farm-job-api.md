# ADR 0012 — API pessoal de Jobs da Farm

## Status

Aceito em 2026-09-02.

## Contexto

A Farm já executava workloads versionados pela interface autenticada, mas não
oferecia uma integração programática segura para automações externas. Reusar o
escopo de leitura permitiria mutações com privilégios excessivos, e uma
tentativa repetida após timeout poderia criar Jobs duplicados.

## Decisão

- Introduzir `FarmExecute`, separado de `FarmRead` e de `IrisExecute`.
- Aceitar somente IDs presentes no catálogo de workloads compartilhado.
- Exigir chave de idempotência de 8 a 120 caracteres na submissão.
- Persistir a chave junto do identificador do token em `farm_jobs` e aplicar
  unicidade no PostgreSQL para resolver também corridas concorrentes.
- Rejeitar o reuso da chave com workload diferente.
- Manter consulta e evidência sob `FarmRead`; submissão e cancelamento sob
  `FarmExecute`.
- Reutilizar o scheduler, as assinaturas, o watchdog e o cancelamento
  cooperativo existentes.
- Não converter CP em AIC nem liquidar propostas econômicas nessa API.

## Consequências

Clientes podem automatizar Jobs sem acesso à sessão web e repetir requisições
com segurança. Tokens somente de leitura continuam incapazes de alterar a
Farm. A limitação única no banco é a autoridade final contra duplicação, e o
histórico preserva qual token originou cada submissão sem armazenar o segredo.
O cancelamento de um Job aceito continua cooperativo e depende da confirmação
do Agent ou da expiração pelo watchdog.
