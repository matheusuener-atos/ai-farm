# ADR 0006 — Orçamento preventivo da Iris por conta

- Status: aceito
- Data: 2026-09-02

## Contexto

A Iris já estima, reserva e liquida AIC no Ledger, mas o limite por tarefa era global para toda a instalação. Uma conta não conseguia definir seu próprio teto diário e mensal, e requisições concorrentes poderiam disputar o saldo sem uma visão de orçamento operacional.

## Decisão

Adicionar uma política opcional e persistente por conta com limites inteiros em mAIC por tarefa, dia UTC e mês UTC. Quando ativa, cada execução cria uma retenção de orçamento antes da reserva financeira. A decisão é serializada por conta no PostgreSQL e considera o consumo liquidado mais todas as retenções ativas.

A retenção possui operação e chave de idempotência únicas, expira após quinze minutos e termina como `Settled` ou `Released`. O custo real substitui a estimativa na liquidação; falha do Ledger ou do Provider libera a retenção. Desativar a política preserva os registros e não remove o limite global de segurança da instalação.

## Consequências

- requisições acima do teto são bloqueadas antes da chamada ao Provider;
- execuções concorrentes não podem reservar além do orçamento disponível;
- os períodos são previsíveis e reiniciam em `00:00 UTC`;
- a política e os registros são isolados pela conta autenticada;
- nenhum CP é convertido, nenhum Reward é criado e nenhuma regra econômica da Farm é alterada.

O painel `/iris/budget` permite configurar a política e acompanhar consumo liquidado, retenções em curso e disponibilidade diária/mensal.
