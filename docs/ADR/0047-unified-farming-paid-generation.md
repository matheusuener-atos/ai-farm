# ADR 0047 — Farmar unificado: pagar a geração de IA com verificação em camadas

## Status

Aceito em 2026-09-05. **A implementação é faseada e o pagamento da geração nasce
DESLIGADO** — esta primeira fatia entrega só a unificação do conceito e a
fundação inerte; o interruptor de pagamento (`AIFarm:Farm:Verification:Enabled`)
só pode ser ligado depois das fatias 3–5 (ver Consequências).

## Contexto

O dono do produto sempre imaginou "Farmar" como **um** conceito: emprestar o
computador à rede para que a IA funcione, e ser pago pelo compute gasto. O código
evoluiu para **dois** conceitos distintos:

- **Farmar** = trabalho determinístico (SHA-256, índice de texto, resposta
  extrativa) que o servidor **reexecuta 1:1** e por isso paga CP;
- **Geração de IA** = rodar o modelo (IRIS_GENERATE) — apresentada como produto
  opcional à parte e, economicamente, valendo **CP = 0**.

O CP=0 da geração é forçado em **um único ponto**:
`PostgresFarmAgentService.SubmitResultAsync` (`job.ComputeUnitsMilli =
unverifiedGeneration ? 0 : definition.ProcessingPointsMilli`), porque
`iris-generate-v1` usa a policy `UNVERIFIED_GENERATION_V1` e
`FarmDeterministicWorkloads.Execute` lança para `IRIS_GENERATE` — o servidor
**não consegue reexecutar** geração. Com CP=0, o guard do
`FarmRewardAutoProposer` (`ComputeUnitsMilli <= 0 => return`) barra a proposta e
a geração nunca chega ao settlement CP→AIC (que já funciona, ADR 0022/0033).

**Limite honesto (física da verificação).** Não existe prova criptográfica barata
por resposta. A geração **não** é bit-determinística — nem na mesma máquina
(temperatura 0.7 sem seed) nem entre máquinas (quantização q4 vs q8, versão do
runtime, GPU vs CPU, ponto-flutuante não-associativo). Logo **não** dá para
reaproveitar a comparação exata de digest que verifica os workloads
determinísticos. Pagar geração exige um modelo de confiança diferente.

## Decisão

**Unificar sob um único "Farmar"** (a geração vira o *modo mais forte* do
farming, não um produto separado) e **pagar a geração por camadas invisíveis de
verificação**, em vez de prova por resposta:

1. **Camada 1 — presença/capacidade/compute (sondas):** micro-tarefas
   determinísticas de resposta-conhecida intercaladas no fluxo do node de
   geração, reexecutadas 1:1 pelo caminho que já existe. Provam que a máquina
   está viva, capaz e gastando compute, e mantêm fresco o sinal `Verified`.
2. **Camada 2 — reexecução amostrada:** uma fração das gerações é reexecutada
   para **auditoria** na plataforma (ou em node confiável) com runtime **pinado**
   e comparação **semântica** (embedding/juiz), **nunca** byte-a-byte.
3. **Camada 3 — quórum/best-of-N (ADR 0045):** a rota premium passa a servir
   também como sinal de auditoria — N nodes **independentes** comparados por
   concordância semântica.
4. **Camada 4 — reputação + escrow/clawback:** o CP é concedido na conclusão
   (baseado em compute, com portão de reputação) e selado na hora para não
   quebrar o fluxo selo→proposta→settlement; a **recompensa** de geração fica em
   **escrow** por uma janela de auditoria — falhar as camadas 2/3 corta
   reputação e o escrow, contendo o dano sem exigir prova síncrona.

A elegância: os "quebra-cabeças" determinísticos deixam de ser um produto à parte
e viram **a etiqueta de preço e o teste de honestidade** do tempo de IA. A
economia e a IA passam a ser um sistema só.

### O que ESTA fatia entrega (seguro, pagamento desligado)

- **Unificação do conceito no painel do Agent**: a seção "Geração de IA
  (opcional)" vira **"Farmar no modo forte"**, com mensagem honesta de que o modo
  forte **ainda não paga CP** (verificação em construção). Ids do JS preservados.
- **Fundação inerte**: novo `FarmVerificationOptions` (`Enabled=false` por
  padrão), fiado por DI e por `AIFarm:Farm:Verification:*`, e a constante
  versionada `VERIFIED_GENERATION_V1` **definida mas não aplicada** a nenhum
  workload. **Nada** toca `SubmitResultAsync`, o manifesto assinado ou a versão
  do agente. Geração continua valendo CP=0.

### Guardrails obrigatórios para a fatia de pagamento (achados da revisão adversarial)

1. **Manifesto assinado.** Trocar a policy do `iris-generate-v1` para
   `VERIFIED_GENERATION_V1` altera um campo do **manifesto assinado**; agentes
   antigos rejeitariam o workload. A troca **exige** bump de
   `FarmAgentProtocol.CurrentAgentVersion` na mesma fatia (e trava agentes
   antigos até atualizarem).
2. **Buraco de cunhagem (self-dealing).** A demanda de geração de uma conta é
   roteada para os nodes **da própria conta** (`SelectExecutionNodeAsync` filtra
   por `accountId`). O mesmo dono controla a pergunta **e** a máquina. Pagar por
   duração auto-reportada sem auditoria = **imprimir CP** com resposta-lixo. Por
   isso o pagamento só liga **depois** das Camadas 2+4 (auditoria + escrow) e com
   **orçamento separado** — nunca com a Camada 1 sozinha.
3. **Portão de reputação.** O enum `FarmNodeReliabilityBand` é
   `Unproven < Watch < Stable < Trusted < Quarantined`: um teste ingênuo
   `banda >= Watch` **inclui Quarantined**. A projeção de reputação também não
   filtra por workload, então geração não-verificada infla a própria banda que
   deveria gate-á-la. A fatia de pagamento deve excluir `Quarantined`
   explicitamente e filtrar as sondas/geração na projeção.

### Parâmetros propostos (inativos até o pagamento ligar)

| Parâmetro | Valor | Razão |
|---|---|---|
| `CpPerSecondMilli` | 6000 (6 CP/s) | ~48 CP numa geração de ~8s ≈ nominal atual (50 CP); só se aplica à geração |
| `MinGenerationCpMilli` | 3000 (3 CP) | piso para respostas curtas |
| `MaxGenerationCpMilli` | 100000 (100 CP) | teto por job, limita padding de duração |
| `RequiredVerifiedFreshnessSeconds` | 900 (15 min) | gate da Camada 1; reusa campos já existentes no node |
| Banda mínima p/ pagar | ≥ Watch (Unproven não paga; Quarantined excluída) | quebra sybil de node novo |
| Fração de auditoria | Watch 0.30 / Trusted 0.07 / HighlyTrusted 0.03 | amostragem determinística por hash do jobId |
| Quórum | N=3, concordância 2-de-3 semântica | reusa `PremiumCandidates`; só válido com runtime pinado |
| Similaridade semântica | cosine ≥ 0.82 ou aceite de juiz | compara significado, nunca bytes |
| Escrow | 48h | janela de auditoria antes de liberar a recompensa |
| Orçamento de geração | separado do determinístico | geração drena o pool ~100x mais rápido |

## Consequências

- **Resolve agora** a confusão "Farmar × geração são duas coisas": o painel passa
  a apresentar um conceito só, com o modo forte como o grau mais alto.
- **Sem regressão e sem buraco**: enquanto `Enabled=false`, geração vale CP=0
  exatamente como hoje.
- **Roadmap para o pagamento** (ordem obrigatória): fatia 2 (sondas) → fatia 3
  (determinismo no fio + identidade forte do modelo: digest+quantização+versão) →
  fatia 4 (auditoria amostrada + escrow/clawback) → fatia 5 (orçamento separado +
  calibração final de CP/seg) → só então `Enabled=true`.
- **Limites documentados**: a auditoria é probabilística respaldada por
  reputação/escrow, **não** é prova; quórum entre nodes do mesmo operador exige
  checagem de independência (IP/ASN/atestação) para valer.

## Perguntas em aberto (decidir antes de ligar o pagamento)

- Onde roda a reexecução de auditoria: GPU da plataforma (mais confiável, custo
  real), nodes confiáveis com runtime pinado, ou só quórum entre pares?
- Pagar por **tempo** (duração, limitável) vs **tokens** (auto-reportável) vs
  híbrido (o mínimo dos dois)? Qual a paridade-alvo AIC/hora geração vs
  determinístico?
- Escrow de 48–72h (UX mais fraca, segurança forte) vs pagar na hora e só cortar
  ganhos/reputação futuros (UX melhor, segurança menor)?
- Rigidez do pin do modelo: exigir digest+quantização+versão idênticos (encolhe a
  pool elegível) vs runtime heterogêneo confiando só na auditoria semântica?
