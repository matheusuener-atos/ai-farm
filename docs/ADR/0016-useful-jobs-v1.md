# ADR 0016 — Useful Jobs v1

## Status

Aceita em 2026-09-02.

## Contexto

Os workloads SHA-256 e Merkle comprovam execução e integridade, mas não entregam
um artefato diretamente consumível pela Iris. A Farm precisa começar a receber
demandas específicas sem abrir execução arbitrária, shell ou acesso à rede.

## Decisão

Adicionar o contrato tipado `IRIS_TEXT_INDEX` (`atos-iris-text-index-v1`) para
texto `STANDARD` de até 32 KiB. O workload produz JSON canônico com métricas,
termos frequentes e hashes de segmentos de até 200 tokens. A entrada é ligada
ao manifesto por SHA-256; o runtime embutido ou o Compute Agent executa a mesma
implementação allowlisted; o servidor reexecuta e compara digest e payload.

O Agent passa a `atos-agent/0.7.0` e o IPC a `atos-worker-ipc-v3`. O processo
filho continua sem credenciais, shell ou rede. O bundle de evidência inclui os
metadados e digests da demanda e da saída, e o download é negado se qualquer
vínculo for adulterado. Agendamentos genéricos não podem selecionar workloads
que exigem payload.

## Consequências

- a Farm executa seu primeiro trabalho útil e verificável para a Iris;
- conteúdo `PRIVATE` e `CONFIDENTIAL` permanece fora do Agent remoto;
- payloads v1 são pequenos e persistidos no PostgreSQL;
- o índice é lexical e determinístico, não um embedding semântico;
- 2,000 CP são técnicos e não originam AIC sem política econômica posterior;
- Object Storage, filas multi-node e inferência útil permanecem próximos passos.
