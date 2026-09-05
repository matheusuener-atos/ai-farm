# ADR 0043 — Modelo da plataforma servido pela rede (geração não verificada)

## Status

Aceito em 2026-09-05.

## Contexto

A intenção original do produto, dita pelo dono: o modelo de IA não pertence
a uma máquina — pertence à plataforma — e o USO dele deve consumir o poder
computacional de toda a rede. A rota extrativa (ADR 0040) construiu o
encanamento (mensagem → demanda → node → resposta → chat), mas não gera
texto. Fisicamente, os pesos não podem morar no web service (plano free) e
fatiar um modelo por token entre máquinas domésticas via internet é
inviável para chat; a topologia honesta é: identidade do modelo central,
pesos replicados nos nodes, execução distribuída pela fila.

## Decisão

1. **O modelo é da plataforma**: o catálogo define `iris-generate-v1`
   (IRIS_GENERATE) com o modelo pinado por configuração da plataforma
   (`AIFarm:AI:NetworkModel`, ex.: `llama3.2:3b`) — o servidor decide qual
   modelo a rede serve; os nodes apenas o hospedam.
2. **Execução pela rede**: a mensagem do chat vira demanda; qualquer node
   confirmado que tenha o runtime local (Ollama em `127.0.0.1:11434`) a
   executa; o Agent chama o modelo diretamente (sem worker sandbox — é uma
   chamada HTTP local, não código não-confiável), com a política de rede
   declarada `LOCALHOST_MODEL_V1`.
3. **Verificação honesta**: política `UNVERIFIED_GENERATION_V1` — o
   servidor NÃO reexecuta (geração não é bit-determinística entre máquinas)
   e aceita o resultado marcando o job como concluído com **CP = 0**. Sem
   verificação, sem crédito: o quórum por par (próximo ADR desta linha)
   trará o CP. Evidência continua selada e o rótulo "não verificada"
   acompanha a resposta no chat.
4. **Teto de execução por manifesto**: o deadline fixo de ~30s do lifecycle
   passa a respeitar o `TimeoutSeconds` do manifesto, limitado a 120s —
   janela necessária para geração em CPU.
5. **Rota no chat**: modelo `AI FARM GENERATE (modelo da rede —
   experimental)` no catálogo composto; falha de node sem runtime cai em
   retry para outro node (MaxAttempts da demanda); sem node apto, resposta
   honesta orientando instalar o Ollama na máquina da pool.

## Consequências

- o uso da IA consome a rede: N conversas em paralelo = N nodes
  trabalhando; a plataforma orquestra, versiona e audita;
- o incentivo fica alinhado: geração só passa a valer CP quando for
  verificável por par — ninguém é pago por trabalho não conferido;
- o Caminho 1 (Ollama como provider único via túnel) continua válido como
  atalho de máquina única; esta rota o generaliza para a pool inteira;
- requisito operacional por node: Ollama instalado com o modelo da
  plataforma baixado (o painel orientará quando o job falhar por ausência).
