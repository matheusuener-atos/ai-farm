# ADR 0026 — Escala do AIC, ordem de buckets e modelo contábil de compra

## Status

Proposta em 2026-09-03 — **aguardando aprovação**. Nada desta decisão está
implementado; a mecânica de crédito administrativo no bucket `Purchased`
(mesma fase) é deliberadamente neutra em relação às alternativas abaixo.

## Contexto

O ADR 0001 deixou três pendências explícitas "antes de qualquer crédito
real": a confirmação da escala do AIC, a ordem de consumo entre `Purchased`,
`Earned` e `Promotional` e o modelo contábil de compra. Desde então:

- a escala `1 AIC = 1.000 mAIC` está codificada em `long` em todo o Ledger e
  nas tarifas internas (750.000/4.500.000 mAIC por milhão de tokens);
- os buckets são codificados no nome da conta contábil
  (`WALLET:{id}:{bucket}:{AVAILABLE|RESERVED}`) e no campo `Bucket` de cada
  lançamento; saldo por bucket é projeção, nunca coluna;
- o consumo (reserva → captura → liberação) saca exclusivamente de
  `Promotional`; `Earned` já recebe crédito real pelo settlement do ADR 0022
  (conta de origem `atos:REWARD_POOL:{versão}:{yyyy-MM}`); `Purchased` passa
  a receber crédito administrativo nesta fase (conta de origem
  `atos:PURCHASED_POOL:ADMIN:v1`), mas nada consome dele;
- o grant promocional de 1.000 AIC por conta não expira.

## Decisão

Proposta concreta, com alternativas consideradas:

### 1. Escala do AIC

- **(A) Manter `1 AIC = 1.000 mAIC`** — recomendada. Já permeia Ledger,
  orçamentos, política de rewards e catálogo de preços; o custo por execução
  é agregado por requisição e arredondado para baixo em mAIC inteiro, então a
  precisão sub-mAIC por token nunca é necessária no Ledger.
- (B) Migrar para `1 AIC = 1.000.000 µAIC` para precisão por token. Rejeitada:
  exigiria migração de todos os lançamentos históricos e nenhum fluxo atual
  precisa dessa granularidade.
- O peg interno de referência (`1 AIC = US$ 0,001`, implícito no catálogo
  OpenAI) permanece referência operacional, sem paridade pública.

### 2. Ordem de consumo dos buckets

- **(A) `Promotional` → `Earned` → `Purchased`** — recomendada. Queima
  primeiro o subsídio (que pode expirar), depois o que foi ganho pela Farm, e
  preserva por último o que foi pago — o bucket com expectativa de reembolso
  e maior custo de confiança.
- (B) `Promotional` → `Purchased` → `Earned`: reconhece receita mais cedo,
  mas gasta dinheiro pago enquanto ainda existe `Earned`; hostil ao usuário.
- (C) Proporcional entre buckets: complica reserva/captura/liberação e a
  auditoria sem benefício claro.
- A liberação e a expiração devolvem sempre ao bucket de origem da reserva;
  uma reserva pode abranger mais de um bucket (lançamentos separados por
  conta contábil, transação única balanceada).

### 3. Expiração do promocional

- **(A) Expiração por lote de grant, apenas para grants futuros** —
  recomendada. Cada grant promocional futuro carrega validade (padrão
  proposto: 180 dias); a expiração gera lançamento explícito
  (débito `atos:PROMOTIONAL_POOL`, crédito `WALLET:...:Promotional:AVAILABLE`
  — estorno balanceado), nunca deleção; lotes já concedidos sem validade não
  expiram. Exige persistir o lote (migration) — só após aprovação.
- (B) Sem expiração (status quo): simples, mas passivo promocional cresce sem
  limite e distorce o simulador econômico.
- (C) Expirar retroativamente os grants existentes: quebra a expectativa das
  contas atuais; rejeitada.

### 4. Modelo contábil de compra

- **(A) Crédito pré-pago (passivo)** — recomendada. A compra credita
  `Purchased` contra uma conta de origem versionada por meio de pagamento
  (`atos:PURCHASED_POOL:{origem}:v1`; hoje só `ADMIN`, futuramente por
  gateway); a receita é reconhecida no consumo (captura para
  `atos:AI_CONSUMPTION`), não na compra. Reembolso, quando existir, é
  lançamento `Refund` balanceado do saldo `Purchased` remanescente.
- (B) Receita no ato da compra: mais simples, porém contabiliza receita de
  serviço não prestado e complica reembolso; rejeitada.

## Consequências

- nenhuma alternativa é implementada antes do aceite formal deste ADR;
- enquanto a ordem (item 2) não for aprovada, `Purchased` e `Earned`
  permanecem **creditáveis mas não consumíveis** — a reserva continua sacando
  só de `Promotional`, e o saldo dos demais buckets fica visível em `/wallet`
  e no Saldo da Farm;
- a aprovação do item 2 exige estender reserva/captura/liberação para
  multi-bucket com testes de concorrência equivalentes aos atuais;
- a aprovação do item 3 exige migration de lotes promocionais e um worker de
  expiração com lançamentos explícitos;
- o item 4 não exige mudança de esquema: contas de origem versionadas seguem
  o precedente do Reward Pool do ADR 0022.
