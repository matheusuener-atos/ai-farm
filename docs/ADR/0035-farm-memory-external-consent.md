# ADR 0035 — Memória da Farm em provider externo com consentimento explícito

## Status

Aceito em 2026-09-04. Revisa parcialmente o ADR 0032 (a exclusividade do
provider local para a memória da Farm deixa de ser incondicional).

## Contexto

O ADR 0032 restringiu a Memória da Farm ao provider local: os índices são
computados a partir de dados da conta pelos nodes da própria Farm, e
provedores externos gratuitos podem usar o que recebem para treinar modelos.
A mensagem de erro já prometia uma "autorização explícita do destino" que
não existia no código. Com o provider gratuito em produção, o dono do
produto decidiu que o foco do sistema — usar a memória computada localmente
nas conversas reais — justifica a liberação, desde que consciente.

## Decisão

O gate em `ChatWorkflow.PrepareCoreAsync` ganha o parâmetro
`authorizeFarmKnowledgeExternally` (padrão `false`, propagado pela UI do
Chat e pela API pessoal via `IrisApiExecutionRequest`):

- sem consentimento, o comportamento anterior permanece: Memória da Farm +
  rota externa é recusada antes de qualquer busca ou chamada;
- com consentimento, os índices fluem para o provider da rota exibida no
  preflight, como parte da instrução de sistema;
- o consentimento é por execução na UI (checkbox aninhado que só aparece com
  a Memória da Farm ligada, zerado a cada carga da página) — nunca uma
  configuração global silenciosa;
- a classificação `PRIVADO` continua soberana: bloqueia qualquer rota
  externa antes do gate da memória, com ou sem consentimento.

## Consequências

- o ciclo central do produto fecha com provider externo: índices farmados
  localmente enriquecem conversas reais no OpenRouter;
- o usuário vê o destino (preflight) e assume o envio explicitamente; a
  proteção por padrão é preservada para quem nunca marcar a autorização;
- a API pessoal expõe o mesmo consentimento (`AuthorizeFarmKnowledgeExternally`),
  mantendo paridade UI/API;
- dados realmente sensíveis têm o caminho de sempre: `PRIVADO`, que nunca
  sai do ambiente local.
