# ADR 0018 — Fila persistente de demandas

## Status

Aceita em 2026-09-02.

## Contexto

Useful Jobs eram submetidos diretamente ao scheduler. Se todos os Compute
Agents estivessem ocupados, pausados ou offline, a solicitação falhava e o
cliente precisava tentar novamente. Isso não representa uma fila real de
trabalho e perde intenção durante indisponibilidade temporária.

## Decisão

Persistir a demanda antes da alocação, com prioridade, prazo de validade,
estado, número de tentativas e diagnóstico. Um worker em background reivindica
itens vencidos por prioridade decrescente e antiguidade crescente. O claim usa
update condicional, de modo que apenas uma instância se torna responsável por
cada tentativa.

Ausência temporária de capacidade devolve o item à fila com backoff exponencial
limitado a cinco minutos. Claims abandonados retornam automaticamente depois de
dois minutos. Erros inesperados encerram a demanda como falha; itens vencidos
expiram; o usuário pode cancelar somente antes do despacho.

Cada Job originado recebe o `DemandId` único. O pipeline procura esse vínculo
antes de alocar capacidade, garantindo retomada exatamente-uma-vez caso haja
falha entre criar o Job e atualizar a demanda. API pessoal também mantém
idempotência própria na aceitação.

## Consequências

- capacidade indisponível deixa de perder demandas;
- prioridades são previsíveis sem preempção de Jobs já iniciados;
- múltiplas instâncias coordenam o consumo pelo PostgreSQL;
- demanda e Job permanecem navegáveis e auditáveis;
- cancelamento não interrompe execução já despachada;
- a fila não converte CP técnicos em AIC nem estabelece recompensa financeira.
