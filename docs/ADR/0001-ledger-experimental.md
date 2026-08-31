# ADR 0001 — Ledger experimental do primeiro incremento

- Status: aceito apenas para protótipo; revisar antes da primeira migration definitiva
- Data: 2026-08-30

## Contexto

O workspace começou vazio. Era necessário provar o ciclo de grant, reserva, execução, captura e liberação sem inventar as decisões econômicas pendentes do produto.

## Decisão

1. A unidade mínima experimental é `mAIC`, com escala versionável de `1 AIC = 1.000 mAIC`. Todos os cálculos usam `long`.
2. Cada `LedgerTransaction` contém ao menos dois `LedgerEntry` e a soma assinada deve ser zero. Saldos são projeções dos lançamentos; não existe campo mutável `Balance`.
3. A reserva move valor de `AVAILABLE` para `RESERVED`. Captura move o custo real para `atos:AI_CONSUMPTION`; a diferença retorna a `AVAILABLE` por uma transação `Release` separada.
4. O fluxo de demonstração consome somente `Promotional`. Isso não define a futura ordem entre `Purchased`, `Earned` e `Promotional`.
5. O runtime usa armazenamento em memória até existir repository PostgreSQL transacional. O `AIFarmDbContext` representa o esquema alvo, não uma alegação de persistência concluída.

## Consequências

- O cenário 1.000 → 20 reservados → 12 capturados → 8 liberados é auditável e testável.
- Repetir a mesma chave não duplica o efeito financeiro.
- Reiniciar o processo perde o estado atual; portanto este incremento ainda não conclui o Marco 1.
- Escala do AIC, ordem de buckets e modelo contábil devem ser confirmados antes da migration definitiva e de qualquer crédito real.
