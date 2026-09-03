# ADR 0027 — Cancelamento interrompe o worker e descarta evidência parcial

## Status

Aceito em 2026-09-03.

## Contexto

O ADR 0012 registrou o cancelamento como cooperativo: o servidor marcava
`CancellationRequestedAtUtc`, o Agent recebia o aviso no heartbeat e o
watchdog encerrava por prazo quando não havia confirmação. O Agent já matava
a árvore do processo filho ao receber o aviso, mas três lacunas permaneciam:
o upload de evidência podia vencer a corrida com um cancelamento recém
solicitado (o `ExecuteUpdateAsync` para `Uploading` não conferia o pedido);
um Job `Cancelled` podia reter colunas parciais de resultado; e o Job Object
do Windows não tinha `TerminateJobObject` importado — a morte da árvore
dependia só de `Process.Kill` e do fechamento do handle.

## Decisão

- o `ExecuteUpdateAsync` que aceita evidência exige
  `CancellationRequestedAtUtc == null`; perdendo a corrida, o upload termina
  o Job como `Cancelled` com o detalhe "evidência parcial descartada";
- `TryTerminateAsync` com status `Cancelled` zera resultado, telemetria do
  worker e saída tipada: um Job cancelado nunca carrega evidência parcial;
- o Agent importa `TerminateJobObject` e o invoca antes de `Process.Kill`
  nos caminhos de cancelamento, timeout e estouro de memória, garantindo a
  morte da árvore inteira mesmo com descendentes fora da enumeração;
- a latência de interrupção permanece limitada pelo heartbeat ativo
  (1–10 s, sugestão do servidor de 2 s) mais o loop de 25 ms do runner;
- o caminho embedded continua cooperativo por token na própria requisição e
  fora do cancelamento remoto, como antes.

## Consequências

- o enunciado "cancelamento continua cooperativo" do ADR 0012 fica superado
  na parte da evidência: a confirmação do Agent segue existindo, mas nunca
  mais aceita upload após o pedido de cancelamento;
- selagem de evidência continua exclusiva do status `Completed`; nada muda
  na verificação profunda;
- cancelamentos seguem sem penalizar o score de confiabilidade (ADR 0023);
- o teste de worker cobre o encerramento de um filho que não coopera
  (bloqueado em stdin) via Job Object.
