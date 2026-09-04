# ADR 0033 — Financiamento inaugural da política de rewards

## Status

Aceito em 2026-09-04.

## Contexto

O ADR 0022 criou o settlement CP → AIC com uma política operacional
versionada, desativada por padrão, que exige três valores inteiros em mAIC.
O mecanismo permaneceu pronto e desligado até o dono do produto aprovar os
números. Com o provider gratuito em produção (ADR 0032), o custo de uma
mensagem da Iris ficou estável em 2 mAIC, criando uma âncora natural para a
taxa de conversão.

## Decisão

Ativar a política `reward-policy-2026-09-v1` com os valores aprovados pelo
dono do produto em 2026-09-04:

- `AicMinorPerThousandCp` = 2.000 mAIC — paridade "1 job verificado
  (1 CP) = 2 mAIC = 1 mensagem da Iris na tarifa simbólica";
- `MonthlyBudgetMinor` = 10.000 mAIC — teto de emissão de 10 AIC por mês;
- `MaximumPerSettlementMinor` = 100 mAIC — trava por settlement contra
  workloads futuros de alto CP drenarem o orçamento de uma vez.

Os valores vivem em variáveis de ambiente (`AIFarm__Farm__Rewards__*`)
declaradas no Dashboard do Render, e ficam documentados no `render.yaml`
como parte do Blueprint.

## Consequências

- o ciclo econômico completo fica operante: job verificado → CP → resgate
  em /farm/rewards → AIC no bucket Earned → consumo na Iris;
- a emissão mensal máxima (10 AIC) é desprezível frente ao saldo
  promocional existente, tornando o período inaugural um ambiente seguro de
  calibração;
- aumentar a generosidade no futuro é uma nova versão de política (gesto de
  melhoria); reduzir também exige nova versão, mas quebra expectativa de quem
  farmou — a assimetria justifica começar conservador;
- orçamento esgotado bloqueia o settlement integralmente (ADR 0022) e o CP
  aguarda o período seguinte; nada é perdido nem parcialmente liquidado.
