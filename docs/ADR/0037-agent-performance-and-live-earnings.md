# ADR 0037 — Aba Desempenho e ganhos ao vivo no painel do Agent

## Status

Aceito em 2026-09-04.

## Contexto

O dono do produto pediu que o Agent mostre em tempo real se a máquina está
ociosa ou entregando poder computacional — como a aba Desempenho do
gerenciador de tarefas — e que a remuneração apareça ao vivo.

## Decisão

- `AgentTelemetry` ganha uma série temporal de utilização (180 amostras de
  1s: CPU% e memória dos workers, jobs ativos), alimentada por uma batida de
  1s do host **inclusive na ociosidade**, para o gráfico mostrar os vales;
- o painel ganha a janela DESEMPENHO: gráfico canvas estilo Macintosh (área
  laranja sobre grade) da utilização relativa à capacidade autorizada, com o
  estado em texto (OCIOSA / ENTREGANDO PODER COMPUTACIONAL) e o percentual
  instantâneo;
- `GET /api/farm/agents/identity` passa a incluir a política de rewards
  (habilitada + taxa por 1.000 CP) e o CP vitalício do node; o painel
  calcula os ganhos da sessão ao vivo (CP verificado × taxa, a mesma
  fórmula do settlement) e mostra o CP total com o caminho de resgate;
- a identidade é renovada a cada 5 minutos pela mesma batida.

## Consequências

- o usuário vê a máquina trabalhar (e ficar ociosa) em tempo real, e quanto
  a sessão já rendeu em AIC estimado pela política vigente;
- a estimativa nunca emite crédito: o resgate continua no fluxo auditável
  de /farm/rewards (proposta → settlement, ADR 0022) — resgate em lote fica
  registrado como melhoria futura;
- a taxa exposta na identidade é informativa e versionada pela política; um
  painel antigo simplesmente não mostra ganhos.
