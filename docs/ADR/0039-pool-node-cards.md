# ADR 0039 — Pool de computadores em cards com uso ao vivo

## Status

Aceito em 2026-09-04.

## Contexto

A página Meu computador listava os nodes como linhas de texto. O dono do
produto desenhou a interface principal da pool como cartas acumuláveis no
estilo da mesa Macintosh: um card quadrado de linha pontilhada "Adicionar um
computador", e um card por máquina — resumo técnico quando desligada,
gráfico de uso do poder computacional quando farmando, e detalhe completo ao
entrar no card.

## Decisão

- **Grid de cards como assentos**: cada card é um assento de máquina; o
  card tracejado é sempre o próximo assento vazio e é NELE que se gera o
  código de vínculo (exibido uma única vez, dentro do próprio card). Cada
  node ocupa um card com nome, status e conteúdo por estado — pendente
  (campo para a chave de confirmação vinda da tela da máquina), offline
  (resumo técnico: CPU/memória, versão, CP total, visto por último) e
  farmando (uso ao vivo + sparkline). Todo card tem "Excluir da rede"
  (descomissionamento do ADR 0038) — o caminho único para refazer chaves.
- **Uso ao vivo via heartbeat**: `FarmAgentHeartbeatRequest` ganha campos
  opcionais `WorkerCpuPercent`/`WorkerMemoryBytes` (o Agent já mede para a
  própria aba Desempenho); o servidor guarda a última amostra no node.
  Agents antigos simplesmente não enviam — o card mostra só os jobs.
- **Sparkline sem tabela nova**: a série do card vem dos jobs concluídos do
  node (CP por minuto na última meia hora), consultada no banco — nenhuma
  telemetria adicional é persistida.
- **Detalhe do node** em `/pool/{nodeId}`: identidade técnica completa,
  confiabilidade, uso ao vivo e os últimos jobs daquela máquina.

## Consequências

- a pool ganha a metáfora visual definitiva — cartas que se acumulam
  conforme a rede cresce — sem quebrar o restante da mesa Macintosh;
- o uso ao vivo no site é honesto: última amostra do heartbeat (2s em
  atividade, 30s ocioso) + série derivada de jobs verificados, nunca números
  inventados;
- protocolos permanecem compatíveis: campos novos são opcionais no
  heartbeat e o snapshot do node só cresce.
