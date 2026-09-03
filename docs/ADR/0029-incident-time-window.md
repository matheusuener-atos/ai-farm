# ADR 0029 — Centro de incidentes por janela de tempo

## Status

Aceito em 2026-09-03.

## Contexto

O ADR 0005 derivava incidentes das 100 entradas mais recentes da auditoria,
50 execuções da Iris e 10 Jobs da Farm. Com multi-node (ADR 0017),
multi-slot (0021), fan-out de até 8 shards (0020) e até 5 tentativas por
shard (0024), uma única demanda pode gerar mais de 10 Jobs — o detector
podia perder falhas inteiras por recência.

## Decisão

- `IncidentQuery` ganha `FromUtc`/`ToUtc`: janela padrão de 7 dias, máxima
  de 30, com validação explícita;
- as fontes deixam de ser "os últimos N" e passam a consultas limitadas à
  janela: integridade pela auditoria paginada (até 500 eventos), falhas da
  Iris e Jobs terminais da Farm por consultas SQL diretas filtradas pelo
  período (até 500 linhas cada, por ordem decrescente);
- ao atingir o teto de qualquer fonte o dashboard reporta `IsTruncated` e a
  UI exibe o aviso com a instrução de reduzir o período — nunca truncamento
  silencioso;
- estado de Node (offline/pausado/quarentena) continua avaliado no instante
  atual, não na janela, porque descreve o presente;
- o reconhecimento valida a chave contra a janela máxima de 30 dias, para
  que um incidente visto na UI possa sempre ser reconhecido.

## Consequências

- incidentes derivados continuam sem tabela própria (só o reconhecimento é
  persistido), preservando o modelo do ADR 0005;
- as chaves de incidente não mudaram: reconhecimentos existentes continuam
  válidos;
- a API `/api/v1/incidents` aceita `fromUtc`/`toUtc`, com erro 400 para
  janelas inválidas;
- um incidente mais antigo que 30 dias deixa de ser detectável — o registro
  histórico permanece na auditoria e no export, que continuam sendo a fonte
  de longo prazo.
