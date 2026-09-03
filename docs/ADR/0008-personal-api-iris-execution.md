# ADR 0008 — Execução da Iris pela API pessoal

- Status: aceito
- Data: 2026-09-02

## Contexto

Os tokens pessoais ofereciam automação somente de leitura. Integrações que
precisam usar a Iris não deveriam contornar o workflow econômico, compartilhar
cookies ou receber acesso implícito ao histórico completo da conta.

## Decisão

Adicionar os escopos independentes `ConversationsRead` e `IrisExecute`. O
primeiro autoriza listar e carregar conversas da própria conta. O segundo
autoriza `POST /api/v1/iris/responses` e pode existir sem acesso de leitura ao
histórico.

Toda execução exige `X-Idempotency-Key` com 8 a 120 caracteres. A chave é
namespaced pelo identificador do token e persistida pelo workflow. Uma repetição
devolve a execução anterior e não chama novamente o Provider, não cria outra
conversa e não movimenta novamente a Wallet. Tokens diferentes podem reutilizar
a mesma chave sem colisão.

A API reutiliza `ChatWorkflow`: valida prompt e conversa, planeja contexto,
seleciona rota, aplica classificação de dados, estima o custo, retém orçamento
e AIC, chama o Provider, liquida o custo real e persiste as mensagens. Portanto,
limites por tarefa, dia e mês, saldo e isolamento por conta são os mesmos do
chat interativo. A rota recebe no máximo 20 requisições por minuto por token e
não aceita antiforgery por ser autenticação Bearer.

## Consequências

- clientes recebem privilégio mínimo separado para histórico e execução;
- retries de rede são economicamente seguros quando preservam a chave;
- a resposta informa operação, conversa, rota, unidades e custos estimado/real;
- conceder `IrisExecute` equivale a autorizar consumo da Wallet dentro dos
  orçamentos vigentes e exige proteção reforçada do segredo;
- anexos, streaming e mutações de conversa permanecem fora desta versão da API.
