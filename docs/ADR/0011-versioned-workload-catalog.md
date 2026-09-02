# ADR 0011 — Catálogo versionado de workloads

- Status: aceito
- Data: 2026-09-02

## Contexto

O Compute Lab reconhecia somente `SHA256_CHAIN` por constantes duplicadas no
servidor e no Agent. Esse bloqueio era seguro, mas cada novo workload exigiria
alterações dispersas e poderia fazer scheduler, worker e verificador aplicarem
políticas diferentes.

## Decisão

Criar o catálogo fechado `atos-workload-catalog-v1` no núcleo compartilhado.
Cada definição congela identificador público, nome interno, versão, algoritmo
de verificação, trabalho, timeout, CPU, memória, CP, rede e classificação.

O scheduler aceita apenas o identificador do catálogo e assina seus valores no
manifesto. O Agent valida todos esses campos antes de iniciar o processo filho.
O IPC `atos-worker-ipc-v2` transporta nome e versão, e o worker resolve
novamente a definição antes da execução. O servidor reexecuta o mesmo algoritmo
e só registra CP quando os digests coincidem.

Além do benchmark `atos-sha256-chain-v1`, o catálogo passa a oferecer
`atos-merkle-root-sha256-v1`, uma árvore de integridade determinística de 4.096
folhas. Nenhum dos dois aceita payload, comando, caminho ou acesso à rede.

## Consequências

- versões desconhecidas ou políticas divergentes são recusadas em todas as
  fronteiras;
- Jobs históricos preservam seus valores e continuam verificáveis;
- a versão mínima do Agent passa a ser `atos-agent/0.6.0`;
- nenhuma migration é necessária porque workload e versão já eram persistidos;
- incluir trabalho arbitrário continua proibido: cada nova capacidade exige
  revisão, versão imutável, executor determinístico e verificador equivalente;
- CP permanece uma medição técnica sem conversão automática para AIC.
