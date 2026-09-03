# ADR 0015 — Agendamentos persistentes da Farm

## Status

Aceito em 2026-09-02.

## Contexto

A Farm executava workloads pelo painel ou pela API pessoal, mas dependia de um
operador ou cliente externo para cada submissão. Automatizar no Render exigia
recorrência persistente, atribuição explícita e proteção contra disparos
duplicados quando mais de uma instância observa a mesma janela.

## Decisão

- Criar `/farm/schedules` para cadastrar, pausar, retomar e disparar
  agendamentos de intervalo fixo em UTC.
- Aceitar somente workloads versionados do catálogo e intervalos de 5 minutos a
  7 dias; nenhum agendamento recebe shell, rede ou payload arbitrário.
- Persistir cada ocorrência em `farm_schedule_triggers` e vincular o Job ao
  agendamento e ao instante planejado.
- Avançar `NextRunAtUtc` e criar o disparo na mesma transação. Uma comparação
  otimista do instante anterior e índices únicos impedem execução duplicada
  entre instâncias.
- Pular janelas vencidas sem produzir rajadas. O próximo instante sempre fica
  no futuro.
- Registrar indisponibilidade ou contenção como `Deferred`, falha inesperada
  como `Failed` e submissão ao pipeline como `Submitted`.
- Recuperar disparos presos em `Dispatching` por mais de cinco minutos como
  falha explícita.
- Reutilizar integralmente o pipeline assinado da Farm. A origem `Scheduled`
  aparece no Centro de Jobs e não altera as regras de evidência ou emissão.
- Expor leitura sob `FarmRead` e mutações sob `FarmExecute`, preservando rate
  limits e isolamento por conta.

## Consequências

Workloads podem operar continuamente sem cron externo e cada tentativa mantém
uma cadeia de atribuição consultável. Pausar não apaga histórico; executar agora
é permitido inclusive como intervenção manual. Uma ocorrência adiada não é
repetida automaticamente, evitando tempestades e sobreposição: o evento explica
o motivo e a recorrência segue para a próxima janela. CP continua sendo contado
somente após conclusão com evidência profundamente válida, e nenhum AIC é
emitido automaticamente.
