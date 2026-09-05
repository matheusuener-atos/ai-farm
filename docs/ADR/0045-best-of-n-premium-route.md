# ADR 0045 — AI FARM PREMIUM: várias máquinas para uma resposta melhor (best-of-N)

## Status

Aceito em 2026-09-05.

## Contexto

O dono do produto queria "somar o poder de N máquinas numa resposta mais
forte". A inferência de UMA resposta não se distribui entre máquinas (ADR
0044: os pesos e o cálculo são inseparáveis; a rede soma vazão, não uma
inferência). O único jeito legítimo de usar várias máquinas para melhorar
UMA resposta é o best-of-N: gerar N candidatas independentes e escolher/fundir
a melhor — o padrão que laboratórios usam para elevar qualidade.

## Decisão

- **Rota `iris-premium-network`** ("AI FARM PREMIUM") no catálogo composto,
  ao lado de GENERATE e NETWORK.
- **best-of-N**: o provider submete N demandas IRIS_GENERATE (padrão 3,
  `AIFarm:AI:PremiumCandidates`, faixa 2–8) com a mesma pergunta, em paralelo
  pela fila — a rede as distribui entre as máquinas com runtime (ADR 0044).
  A diversidade vem da temperatura 0,7 do runtime, então funciona mesmo com
  uma máquina só (candidatas sequenciais e distintas).
- **Juiz**: com ≥2 candidatas, uma geração final (também na rede) sintetiza
  a melhor resposta a partir das candidatas. Com 1 candidata, devolve-a
  direto; com 0, erro honesto orientando ativar a geração. Juiz indisponível
  cai na candidata mais longa (melhor-esforço), nunca falha.
- **Custo**: tarifa premium simbólica (2.000 mAIC/M, ~4× a GENERATE), pois
  usa N+1 execuções da rede. Continua ordens de magnitude abaixo de nuvem
  paga. Os jobs internos seguem CP=0 (não verificados, ADR 0043).
- **Latência honesta**: as N candidatas rodam em paralelo entre máquinas, então
  o relógio ≈ 1 geração + 1 juiz (~2×), não N×; com uma máquina só, é ~N+1×.
- **N adaptativo (justiça do pool)**: premium ocupa máquinas que poderiam
  atender outros chats. Sem geração concorrente, usa o N cheio; sob carga,
  N cai pela capacidade livre de geração (`livres = nodes capazes − gerações
  ativas`), e sem folga degrada para geração simples (N=1). Assim um chat
  premium nunca amarra o pool inteiro nem atrasa os chats normais.

## Consequências

- "mais máquinas = respostas melhores" vira realidade concreta: cada máquina
  extra amplia o leque de candidatas e a capacidade paralela;
- a experiência segue centralizada (o usuário escolhe PREMIUM e recebe uma
  resposta); o trabalho é repartido pela rede;
- é a tradução fiel e possível do sonho "somar poder para IA mais forte",
  dentro do que a física permite;
- próximo passo independente: modelos fortes nas máquinas potentes (roteamento
  por capacidade de modelo), que combina com o premium.
