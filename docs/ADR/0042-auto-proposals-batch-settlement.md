# ADR 0042 — Propostas automáticas e resgate em lote

## Status

Aceito em 2026-09-05.

## Contexto

O caminho CP → AIC exigia, por job: salvar uma fotografia econômica medida
naquele job, registrar a proposta e liquidá-la — três cliques por job. Com a
farm contínua e a rota da rede gerando dezenas de jobs por hora, o resgate
manual por proposta se tornou inviável, deixando CP verificado sem destino.

## Decisão

- **A última fotografia vale como política pessoal**: quando um job
  verificado completa (embedded ou Agent) e a conta possui ao menos uma
  fotografia econômica salva, a última é reaplicada com o CP/h medido DESTE
  job — nova fotografia selada no job — e a proposta nasce automaticamente
  (`FarmRewardAutoProposer`, melhor-esforço: nunca interrompe a conclusão).
  Sem fotografia-base, nada acontece: a automação é opt-in pelo primeiro
  "Salvar fotografia".
- **Caminhos por conta explícita**: os stores de cenário e proposta ganham
  variantes internas `...ForAccountAsync`, porque a conclusão de um job de
  Agent não carrega identidade de usuário; os métodos públicos continuam
  identity-scoped e delegam.
- **Resgate em lote**: `SettleAllAsync` percorre as propostas não liquidadas
  em ordem de criação, reusando o `SettleAsync` atômico e idempotente de
  cada uma; para com motivo explícito quando a política/orçamento bloqueiam
  e pula (sem mascarar) proposta individual inválida. Botão "Resgatar tudo"
  em /farm/rewards.

## Consequências

- o ciclo fecha sem fricção: job verificado → proposta automática → um botão
  liquida a fila inteira dentro do orçamento;
- todas as invariantes do ADR 0022 permanecem: settlement único por
  proposta, lock por política/período, orçamento mensal, partida dobrada;
- a fotografia automática fica auditável como qualquer outra (nome
  "auto {workload}", selada na evidência do job).
