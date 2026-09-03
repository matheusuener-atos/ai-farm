# ADR 0028 — Agregação de analytics no banco

## Status

Aceito em 2026-09-03.

## Contexto

Os ADRs 0009 e 0010 registraram como dívida: as consultas de analytics da
Iris e da Farm carregavam duas janelas inteiras de execuções em memória e
agregavam no processo. Na Farm, cada linha vinha com payloads de manifesto e
evidência e todos os eventos de todos os Jobs, para reverificar cada
`Completed`. A auditoria ainda buscava eventos de 5.001 Jobs antes de
aplicar o teto de 5.000.

## Decisão

- Iris: as agregações por dia, rota e status, a contagem distinta de
  conversas e o custo da janela anterior rodam como `GROUP BY`/`SUM` no
  PostgreSQL; apenas amostras estreitas de duração da janela atual chegam em
  memória. O montador `FromAggregates` no Core reproduz exatamente o
  contrato do `Build` — garantido por teste de paridade;
- Farm: todos os Jobs das duas janelas chegam em projeção estreita (sem
  payloads); os payloads e eventos são carregados somente para os Jobs
  `Completed`, únicos que exigem a verificação profunda por linha;
- a reverificação por linha **não** é pré-agregável: substituí-la por uma
  tabela de sumário gravada na selagem faria as métricas confiarem num
  veredito antigo e deixarem de denunciar adulteração posterior do banco —
  alternativa considerada e rejeitada;
- auditoria: o teto de 5.000 por categoria permanece (sinalizado na UI e no
  export via `IsScanTruncated`), e os eventos de Jobs passam a ser buscados
  apenas para os registros dentro do teto.

## Consequências

- o custo de memória da Iris cai de duas janelas completas para agregados e
  amostras de duração; o da Farm cai para payloads/eventos apenas de
  `Completed`;
- o teste que adultera um evento e espera `IntegrityFailureCount` continuar
  subindo permanece válido — a tamper-evidence das métricas foi preservada;
- quando o volume de `Completed` justificar, uma tabela de sumário por Job
  exigirá um novo ADR explicitando a troca de garantias.
