# ADR 0021 — Compute Agent multi-slot com reservas de recursos

## Status

Aceita em 2026-09-03.

## Contexto

Demandas distribuídas já permitiam que vários Nodes colaborassem, mas cada Node
mantinha no máximo um Job ativo. Uma máquina com várias CPUs e memória ociosa
continuava executando apenas um worker, portanto a capacidade autorizada não se
convertia em paralelismo efetivo.

## Decisão

Cada Job persiste uma reserva positiva de CPU lógica e memória, derivada do
requisito versionado do workload e repetida no manifesto assinado. O índice
único de Job ativo por Node é removido. Sob o advisory lock da conta, o
scheduler soma as reservas de todos os estados ativos, calcula o saldo do Node
e só cria um novo Job quando esse saldo cobre integralmente a próxima reserva.

O protocolo `atos-agent/0.8.0` passa a reportar a coleção de Jobs ativos no
claim e no heartbeat. O servidor renova cada lease e devolve uma coleção de
cancelamentos. O coordenador local mantém um processo worker por Job, soma os
limites de todos os manifestos e recusa uma oferta que exceda a capacidade
residual da política local. Encerramento do Agent cancela e aguarda todos os
workers.

## Consequências

- uma máquina pode processar vários shards ou Jobs simultaneamente;
- CPU e memória autorizadas são limites agregados, não apenas limites por Job;
- Nodes parcialmente ocupados continuam elegíveis na matriz de capacidade;
- cada worker preserva manifesto, timeout, contenção, lease e evidência próprios;
- Agents anteriores a `0.8.0` precisam ser atualizados antes de receber Jobs;
- reduzir limites não mata Jobs existentes, mas bloqueia novos claims;
- reserva é admission control e não comprova disponibilidade física, GPU ou
  ausência de interferência de outros processos do sistema operacional.
