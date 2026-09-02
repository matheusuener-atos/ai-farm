# ADR 0002 — Simulador econômico da Farm

- Status: experimento; não estabelece política comercial
- Data: 2026-09-02

## Contexto

O Compute Lab já mede CU e produz evidência assinada, mas CU técnico não cria
automaticamente capacidade financeira para recompensar o Provider.

## Decisão

A primeira prova da Fase 4 é um simulador puro e determinístico. Ele combina
capacidade, utilização, sucesso, energia e deduções econômicas para calcular o
reward máximo financiável em BRL. Os cenários não são persistidos e não alteram
a Wallet ou o Ledger.

Não existe conversão CU → AIC neste incremento. Valores iniciais são exemplos
editáveis, não preços ou promessas. Reward real permanece bloqueado até a fonte
de receita, o orçamento e a política de conversão serem aprovados.

## Equação

```text
reward máximo = valor do compute verificado
               - energia
               - verificação
               - plataforma
               - impostos e taxas
               - margem necessária
               - reserva de risco
```

Quando as deduções excedem o valor verificado, o reward é limitado a zero e a
diferença é mostrada como déficit.
