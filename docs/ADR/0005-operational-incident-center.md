# ADR 0005 — Incidentes operacionais derivados e reconhecimento persistente

- Status: aceito
- Data: 2026-09-02

## Contexto

O Centro de Auditoria torna sinais técnicos consultáveis, mas ainda exige que o
operador encontre manualmente situações que pedem atenção. Falhas de
integridade, erros da Iris, Jobs malsucedidos e indisponibilidade do Node devem
ser destacados sem modificar as fontes imutáveis.

## Decisão

Derivar incidentes das projeções e serviços existentes. Incidentes críticos
incluem falhas de integridade, execuções malsucedidas da Iris, Jobs `Failed` ou
`TimedOut` e Node offline. Cancelamentos, rejeições e Node pausado são alertas.

Persistir somente o reconhecimento, identificado por uma chave estável e única
por conta. Reconhecer registra data e autoria implícita da conta autenticada;
não altera a fonte, não resolve automaticamente o problema e é idempotente.

## Consequências

O painel `/incidents` permite filtrar, priorizar e reconhecer ocorrências. A
detecção usa as 100 entradas mais recentes da auditoria, 50 execuções da Iris e
10 Jobs da Farm, tornando explícito que este é um painel operacional recente e
não substitui a exportação histórica da auditoria.

Nenhuma regra financeira, recompensa ou conversão CP → AIC é criada por este
recurso.
