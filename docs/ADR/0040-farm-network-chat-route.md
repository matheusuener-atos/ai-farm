# ADR 0040 — Rota "AI Farm Network": o chat como fonte real de trabalho

## Status

Aceito em 2026-09-04.

## Contexto

O dono do produto identificou a dissonância central: "executar jobs" não é o
objetivo — o objetivo é usar o chat consumindo o poder computacional da
rede. Dos workloads existentes, só o IRIS_TEXT_INDEX produz valor direto
para o chat; benchmarks e marathon são andaimes do ciclo verificado. O chat
deveria SER os jobs.

A restrição estrutural é a verificação: a tamper-evidence da farm é a
reexecução determinística, e inferência generativa (LLM) não é determinística
entre CPUs distintas — verificá-la exige quórum redundante e reputação
(base nos ADRs 0020/0024), um salto que não se improvisa.

## Decisão

1. **Nova rota de chat "AI Farm Network"** (modelo `iris-answer-extractive`):
   selecionável no perfil da conversa. A mensagem vira uma demanda útil da
   própria conta; um node da pool a executa; a resposta volta ao chat com as
   fontes. CP do job pertence ao node que computou — o ciclo fecha sem
   provider externo.
2. **Workload `iris-answer-v1`** (IRIS_ANSWER, `DETERMINISTIC_OUTPUT_V1`,
   5 CP): motor extrativo determinístico — recebe pergunta + trechos da
   biblioteca (recuperados no servidor pela busca lexical existente),
   pontua, extrai e compõe a resposta com referências. 100% verificável por
   reexecução barata; DENY_ALL preservado; caps de 32KB de entrada e o
   deadline de 30s respeitados.
3. **Provider roteado**: um `RoutingAiProvider` despacha por rota — "AI Farm
   Network" para o provider da farm (submete a demanda e acompanha até
   concluir, ~30-90s no pior caso pela cadência de heartbeat), qualquer
   outra rota para o provider externo configurado. Tarifa própria no
   catálogo de preços (simbólica), reserva/captura no ledger como sempre.
4. **Honestidade no rótulo**: o v1 responde COM O QUE A REDE INDEXOU — é
   extração sobre a biblioteca da conta, não um LLM. Perguntas fora da
   biblioteca recebem a resposta honesta de que a rede ainda não tem aquele
   conhecimento, com o caminho (indexar conteúdo ou usar a rota externa).
5. **v2 declarado**: inferência generativa nos nodes (modelo pequeno
   embarcado, decodificação gulosa, verificação por quórum de N nodes +
   reputação/quarentena, rede local permitida por política explícita) fica
   registrado como o próximo ADR desta linha — exige revisar a política de
   verificação e não entra disfarçado.

## Consequências

- o gesto do usuário passa a ser o desenho original: conversar consome a
  rede; indexar alimenta a rede; o CP nasce de trabalho que serve ao chat;
- benchmarks e marathon permanecem como calibração e consumo sob demanda,
  não como a cara do produto;
- a latência da rota é a da fila (segundos a ~1 min) — o chat mostra o
  progresso; o claim de longo-poll fica como melhoria;
- a biblioteca vira o ativo central: quanto mais a conta indexa, melhor a
  rede responde.
