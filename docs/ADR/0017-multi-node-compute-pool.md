# ADR 0017 — Compute Pool multi-node

## Status

Aceita em 2026-09-02.

## Contexto

O modelo anterior possuía unicidade de Node e de Job ativo por conta. Embora o
protocolo remoto fosse real, uma conta não conseguia usar duas máquinas ao
mesmo tempo, portanto ainda não existia uma Farm distribuída.

## Decisão

Permitir vários Nodes por conta, preservando no máximo um Node embedded e um
nome único por máquina. Re-enrollment do mesmo nome rotaciona aquela credencial;
um nome novo cria outro Agent. A exclusividade de Job ativo passa da conta para
o Node.

O scheduler considera somente Agents online, na versão exigida, com heartbeat
atual, política local ativa e sem Job em andamento. A distribuição escolhe o
Node menos recentemente utilizado, com desempate estável por identificador.
Quando a conta possui Agents, o embedded não recebe fallback silencioso.

A etapa de alocação é serializada por advisory lock transacional derivado do ID
da conta. O lock termina depois da persistência do Job; a execução continua em
paralelo nos Nodes. Dashboard e API passam a expor o inventário completo e
permitem pausar ou retomar cada Node isoladamente.

## Consequências

- duas máquinas podem receber e concluir Jobs simultaneamente;
- uma máquina continua executando no máximo um Job por vez;
- múltiplas instâncias do servidor coordenam a seleção pelo PostgreSQL;
- Agents indisponíveis não provocam execução inesperada no host web;
- esta fase ainda não inclui prioridades, GPUs ou filas com reserva de recurso.
