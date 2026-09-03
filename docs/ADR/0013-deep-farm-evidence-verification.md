# ADR 0013 — Verificação profunda de evidências da Farm

## Status

Aceito em 2026-09-02.

## Contexto

O bundle concluído já possuía digest e HMAC, mas validar somente o payload
selado não prova que o registro atual do Job e sua trilha de eventos continuam
iguais ao que foi assinado. Uma alteração posterior no PostgreSQL poderia
preservar uma assinatura válida sobre o payload antigo enquanto apresentava
estado divergente em consultas, analytics ou propostas econômicas.

## Decisão

Toda evidência passa a produzir quatro diagnósticos independentes:

- `SignatureValid`: digest, HMAC, algoritmo e identidade da chave;
- `ManifestValid`: assinatura e vínculo integral do manifesto com o Job;
- `JobBindingValid`: identidade, workload, digests, CP, telemetria e horários;
- `LifecycleValid`: sequência contígua e igualdade exata dos eventos atuais
  com os eventos congelados no bundle, incluindo a conclusão terminal.

`IsValid` exige que os quatro sejam verdadeiros. A verificação profunda é
usada pela projeção do Compute Lab e pela API pessoal. Download de evidência,
Centro de Auditoria, saldo técnico, analytics, criação de fotografias econômicas
e propostas de reward também usam o mesmo gate.

## Consequências

A interface distingue a causa de uma falha de integridade, e uma assinatura
criptograficamente correta já não mascara divergências no estado persistido.
Eventos ausentes, adicionais, reordenados ou alterados invalidam o lifecycle.
Alterações em campos técnicos ou econômicos invalidam o vínculo do Job. Jobs
legados sem bundle continuam explicitamente sem evidência, sem serem tratados
como válidos. Nenhuma migration é necessária porque a fonte canônica continua
sendo o bundle já armazenado e as tabelas atuais.
