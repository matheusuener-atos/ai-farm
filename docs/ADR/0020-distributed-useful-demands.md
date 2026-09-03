# ADR 0020 — Demandas úteis distribuídas com fan-out/fan-in

## Status

Aceita em 2026-09-03.

## Contexto

O Compute Pool conseguia executar Jobs independentes em Nodes distintos, mas
cada demanda útil ainda produzia exatamente um Job. Portanto, adicionar
máquinas aumentava a vazão da fila, sem permitir que elas colaborassem na mesma
entrada. Era necessário distribuir trabalho real preservando idempotência,
evidência verificável e recuperação após falhas do processo web.

## Decisão

Demandas `IRIS_TEXT_INDEX` aceitam entre um e oito shards. A entrada validada é
particionada deterministicamente por tokens contíguos; entradas pequenas usam
menos shards para nunca criar uma parte vazia. Demanda e shards são persistidos
antes do despacho, com sequência, payload, tamanho e digest imutáveis.

Cada shard gera um Job independente, identificado unicamente por demanda e
sequência. O dispatcher pode parar por falta de Nodes e continuar depois sem
recriar Jobs existentes. Um Job criado antes de uma queda é reencontrado por
essa identidade e religado ao shard. Depois do fan-out, a demanda permanece em
reconciliação até todos os Jobs chegarem a um estado terminal.

O fan-in aceita somente Jobs concluídos que tenham saída e bundle de evidência
válido. Os índices são ordenados pela sequência, desserializados, somados e
serializados no formato canônico `atos-distributed-text-index-v1`; payload e
SHA-256 consolidados ficam persistidos na demanda. Qualquer shard terminal não
aprovado encerra a demanda como falha, sem produzir saída parcial.

## Consequências

- uma única demanda passa a usar vários Nodes e produz uma saída verificável;
- retentativas retomam apenas shards ausentes e toleram queda entre Job e vínculo;
- conteúdo e evidência continuam limitados ao escopo da conta autenticada;
- o protocolo do Agent não muda: cada shard é um Useful Job comum;
- o paralelismo real depende da quantidade de Nodes elegíveis disponíveis;
- a divisão por tokens não preserva espaços originais entre shards, mas o índice
  textual é determinístico e não depende deles;
- cada Job aprovado registra seu CP técnico; o agregador não emite CP ou AIC
  adicional e não estabelece conversão econômica;
- redução distribuída de embeddings, inferência e artefatos grandes permanece
  fora desta versão.
