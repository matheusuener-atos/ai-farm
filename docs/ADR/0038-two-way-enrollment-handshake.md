# ADR 0038 — Vínculo com contrapartida: handshake de mão dupla

## Status

Aceito em 2026-09-04.

## Contexto

O vínculo do Compute Agent era de mão única: um código de uso único gerado
no site bastava para uma máquina entrar na conta e começar a receber
trabalho. Um código vazado (print, área de transferência, ombro) vinculava
uma máquina de terceiro sem o dono perceber. O dono do produto pediu a
contrapartida: a máquina informa o e-mail da conta e o código do site;
dando certo, ela gera uma chave de confirmação que o dono digita no site —
só então a máquina entra na pool.

## Decisão

1. **Enroll com e-mail**: `FarmAgentEnrollmentRequest` ganha `AccountEmail`
   obrigatório; o servidor compara com o e-mail do dono do código (usuário
   Identity da conta). Divergência recusa o vínculo antes de criar node.
2. **Node nasce pendente**: o enroll grava `ConfirmationCodeHash` e deixa
   `ConfirmedAtUtc` nulo. A resposta traz a chave de confirmação em claro
   (formato humano `XX-XXXX`), exibida em destaque no painel da máquina.
3. **Confirmação no site**: o card pendente na pool pede a chave; o POST
   compara o hash e sela `ConfirmedAtUtc`. A chave não expira sozinha, mas
   um novo vínculo da mesma máquina gira código e hash.
4. **Pendente não farma**: claim (inclusive o top-up da farm contínua)
   exige `ConfirmedAtUtc`; o heartbeat responde `AcceptJobs=false` com
   sinalização de pendência para o painel mostrar "aguardando confirmação
   no site" com a chave.
5. **Chaves nunca são reexibidas**: código do site e chave de confirmação
   aparecem uma única vez, cada um no seu lado. Perdeu? Exclui a máquina da
   rede (descomissionamento: credencial revogada e assento liberado, com o
   histórico de jobs preservado) e adiciona de novo — novo assento, chaves
   novas. Um node descomissionado nunca volta a autenticar.
6. **Compatibilidade**: nodes existentes são selados na migration
   (`ConfirmedAtUtc = agora`) — nada re-vincula à força. Binários antigos
   não enviam e-mail e são recusados no enroll com mensagem clara de
   atualização (bump para `atos-agent/0.10.0`); sessões já vinculadas não
   são afetadas.

## Consequências

- posse dupla comprovada: quem vincula tem o código do site E quem confirma
  vê a chave na tela da máquina — um código vazado sozinho não faz nada;
- a pendência vira estado de primeira classe da interface da pool (card
  "aguardando confirmação"), alinhada aos cards do ADR 0039;
- o fluxo do leigo continua de dois toques: colar o código na máquina,
  digitar a chave de volta no site.
