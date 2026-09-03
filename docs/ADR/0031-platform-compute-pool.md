# ADR 0031 — Pool de computação da plataforma

## Status

Aceito em 2026-09-03.

## Contexto

Todo despacho respeitava o isolamento total por conta: um node só executava
demandas da própria conta. Para "deixar o poder computacional à disposição da
plataforma" era preciso relaxar esse invariante em exatamente um ponto, sem
tocar na verificação, na evidência ou na economia. Três fatos do código
tornaram isso barato: o claim do Agent é keyed apenas por Node (não por
conta), o fan-in de demandas nunca filtra jobs por conta, e a posse do job
determina sozinha para quem contam CP, evidência e settlement.

## Decisão

- `AIFarm:Farm:PlatformAccountId` configura a única conta cujo trabalho pode
  cruzar fronteira de conta; sem configuração o pool está desligado e nada
  muda;
- o dono de um Compute Agent faz **opt-in explícito** por node
  (`farm_nodes.SharePlatformWork`, padrão falso), pela UI (`/farm` e
  `/meu-computador`) ou pela API (`POST /api/v1/farm/nodes/{id}/share` e
  `/unshare`, escopo `FarmExecute`); nodes embedded nunca participam;
- o despacho de uma demanda da conta-plataforma ocorre em duas fases: a fase
  A escolhe a conta dona de um Agent compartilhado apto (online, heartbeat
  fresco, versão atual, capacidade suficiente, failover respeitando o node
  excluído); a fase B agenda dentro do advisory lock desse dono, restrita aos
  seus Agents compartilhados — locks e contabilidade de reservas existentes
  permanecem corretos;
- **o Job pertence à conta do dono do node**, não à plataforma: CP
  verificado, evidência, saldo técnico, propostas e settlement do trabalhador
  seguem os fluxos normais sem nenhuma mudança; o replay por
  (demanda, shard, tentativa) passa a ser global, garantido pelo índice
  único existente;
- a demanda continua da plataforma: consolidação, reconciliação e download da
  saída funcionam inalterados porque nunca filtraram por conta; o trabalhador
  vê o job (origem "Demanda", rótulo da demanda visível) e a plataforma **não**
  vê o job na superfície da conta alheia — apenas a própria demanda e a saída
  consolidada;
- só workloads do catálogo fechado participam, com as regras vigentes de
  classificação (`IRIS_TEXT_INDEX` remoto segue restrito a `STANDARD`).

## Consequências

- é a Etapa 1 do compute compartilhado: uma única compradora (a plataforma),
  sem preços nem marketplace; compute entre contas de terceiros exigirá um
  ADR próprio com modelo de governança;
- o conteúdo da demanda da plataforma é visível ao trabalhador que a executa
  (rótulo e payload do shard) — por isso apenas conteúdo não sensível deve
  ser submetido pela conta-plataforma;
- os pontos ganhos em trabalho da plataforma só viram AIC pelo settlement do
  ADR 0022, quando a política estiver financiada — o pool dá ao reward pool o
  seu motivo econômico;
- a seleção da fase A é por frescor de heartbeat; justiça de distribuição
  entre voluntários (round-robin/quotas) fica como refinamento futuro;
- desligar o compartilhamento vale para novos despachos; jobs já aceitos
  terminam normalmente.
