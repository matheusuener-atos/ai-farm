# ADR 0007 — Tokens pessoais para a API da Iris

- Status: aceito
- Data: 2026-09-02

## Contexto

A aplicação possuía autenticação interativa por cookie e credenciais específicas do Compute Agent, mas não havia uma forma segura e limitada de integrar dashboards, scripts ou automações de leitura.

## Decisão

Introduzir tokens pessoais prefixados por `iris_pat_`, com 256 bits de entropia. O segredo completo é devolvido uma única vez; o banco armazena apenas SHA-256 e um prefixo público para identificação. A validação usa comparação em tempo constante e exige conta ativa, token não revogado e não expirado.

Cada token recebe um ou mais escopos independentes: `ProfileRead`, `UsageRead`, `IncidentsRead` e `AuditRead`. A validade máxima é 366 dias. Rotas `/api/v1/*` usam exclusivamente o esquema Bearer da Iris, políticas por escopo e limite de 120 requisições por minuto por token. Cookies não satisfazem essas políticas e tokens da Iris não substituem credenciais do Compute Agent.

Emissão, estado e revogação aparecem na categoria `Security` do Centro de Auditoria. O último uso é atualizado sem registrar payload, prompt, resposta ou o segredo.

## Consequências

- vazamento do banco não revela tokens reutilizáveis;
- revogação e expiração impedem novas autenticações imediatamente;
- integrações operam com privilégio mínimo;
- esta versão oferece somente leitura e não permite executar a Iris, alterar Wallet, reconhecer incidentes ou controlar Nodes.
