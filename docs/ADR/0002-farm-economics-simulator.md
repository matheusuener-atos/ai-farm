# ADR 0002 — Simulador econômico da Farm

- Status: experimento; não estabelece política comercial
- Data: 2026-09-02

## Contexto

O Compute Lab já mede CU e produz evidência assinada, mas CU técnico não cria
automaticamente capacidade financeira para recompensar o Provider.

## Decisão

A primeira prova da Fase 4 é um simulador puro e determinístico. Ele combina
capacidade, utilização, sucesso, energia e deduções econômicas para calcular o
reward máximo financiável em BRL. Os cenários não são persistidos e não alteram
a Wallet ou o Ledger.

Não existe conversão CU → AIC neste incremento. Valores iniciais são exemplos
editáveis, não preços ou promessas. Reward real permanece bloqueado até a fonte
de receita, o orçamento e a política de conversão serem aprovados.

## Equação

```text
reward máximo = valor do compute verificado
               - energia
               - verificação
               - plataforma
               - impostos e taxas
               - margem necessária
               - reserva de risco
```

Quando as deduções excedem o valor verificado, o reward é limitado a zero e a
diferença é mostrada como déficit.

O simulador pode adotar a vazão do último benchmark concluído da conta como
capacidade medida em CU/h. A referência mostra Job, duração, Node e estado da
evidência; os demais parâmetros continuam sendo hipóteses informadas pelo
usuário.

O ponto de equilíbrio calcula o menor valor bruto por 1.000 CU que cobre
energia e deduções percentuais. A análise de sensibilidade exibe três cenários
transparentes — stress, base e favorável — para impedir que uma única projeção
seja interpretada como promessa.

Fotografias escolhidas pelo usuário são persistidas como registros append-only
e isolados por conta. Cada registro congela os JSONs de entrada e resultado,
versão, horário, Job/evidência de origem e um digest SHA-256 que inclui a conta.
Recarregar uma fotografia cria uma cópia editável; salvar novamente acrescenta
outro registro em vez de alterar o histórico.
