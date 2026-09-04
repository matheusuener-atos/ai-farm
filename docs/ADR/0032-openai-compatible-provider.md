# ADR 0032 — Provider compatível com chat-completions

## Status

Aceito em 2026-09-04.

## Contexto

A Iris suportava dois providers: Fake e OpenAI (Responses API). Para operar
gratuitamente era preciso um modelo open source ou chinês — e praticamente
todo o ecossistema (OpenRouter, Groq, DeepSeek, Zhipu/GLM, MiniMax, Ollama
local) fala o mesmo dialeto: o chat-completions da OpenAI. Integrar um
modelo específico congelaria uma escolha volátil; integrar o dialeto abre
todos de uma vez.

## Decisão

- novo provider `AIFarm:AI:Provider = OpenAICompatible`, com endpoint
  configurável (`AIFarm:AI:Compatible:Endpoint`), modelo, rótulo exibido,
  janela de contexto e limite de resposta; a chave (`COMPATIBLE_API_KEY`) é
  opcional para endpoints locais como o Ollama;
- o preço continua sendo tarifa interna em mAIC (o ciclo
  reserva → captura → liberação exige custo positivo): modelos gratuitos
  usam tarifa simbólica (padrão 1.000 mAIC por milhão de tokens — 1 AIC/M);
  a origem do preço é operacional, não paridade pública;
- streaming via SSE do chat-completions com `stream_options.include_usage`;
  a medição de uso do provider é obrigatória — resposta sem `usage` é falha,
  nunca custo inventado;
- anexos são recusados com mensagem clara (o dialeto multimodal varia entre
  provedores; fica para quando houver demanda);
- os erros usam o rótulo configurado ("O OpenRouter atingiu um limite…") e
  mapeiam sem créditos/cota para `QuotaExhausted`, não retryable;
- o teto diário de segurança passa a valer para qualquer provider real
  (OpenAI ou compatível);
- as salvaguardas de classificação continuam: o provider compatível é
  externo, então `PRIVADO` nunca chega a ele e a memória da Farm permanece
  exclusiva do provider local.

## Consequências

- operar de graça é configuração, não código: OpenRouter expõe modelos
  `:free` (incluindo chineses como GLM e MiniMax) com limites de ~20 req/min
  e 50 req/dia — adequado ao uso pessoal, documentado no README;
- modelos gratuitos hospedados podem usar os prompts para treino conforme a
  política do provedor — mais um motivo para a classificação `PADRÃO` ser a
  única aceita em rota externa gratuita;
- trocar de modelo/endpoint deve versionar `PriceCatalogVersion`, como no
  provider OpenAI;
- um único provider ativo por instalação permanece a regra; multi-rota
  simultânea exigiria evolução do catálogo e fica fora desta decisão.
