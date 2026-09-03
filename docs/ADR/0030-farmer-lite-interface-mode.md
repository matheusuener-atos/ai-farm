# ADR 0030 — Farmer Lite: modo de interface por conta

## Status

Aceito em 2026-09-03.

## Contexto

O produto acumulou 24 telas com vocabulário técnico (manifesto, settlement,
evidência, partidas dobradas) que um usuário leigo não precisa entender para
usar o essencial: conversar com a Iris, acompanhar créditos e colocar o
computador para trabalhar. O dono do produto pediu uma versão "farmer-lite"
com o mesmo visual Macintosh e linguagem clara, mantendo a completa
("farmer-pro") intacta.

## Decisão

- Um único app, nunca um fork: a preferência `InterfaceMode` (`Lite`/`Pro`)
  vive na conta (`accounts.InterfaceMode`, constraint de valores), exposta
  por `IInterfaceModeService` e propagada às páginas por CascadingValue;
- contas novas nascem em `Lite`; a migration preservou todas as contas
  existentes em `Pro` (backfill), e a troca é livre no menu Conta
  ("Ativar modo simples/completo");
- no Lite a navegação reduz a Início, Conversar, Créditos, Meu computador e
  Conta; as telas Início e Wallet ganham variantes de linguagem clara
  ("créditos", "pontos de trabalho", "seu computador"), e o Chat oculta os
  controles técnicos (contexto, classificação, memória da Farm, export JSON)
  usando os padrões seguros;
- a página `/meu-computador` condensa o vínculo do Agent em três passos
  leigos (baixar programa, gerar código, deixar rodando) e permite disparar
  uma tarefa de teste; ela também aparece no menu Farm do modo Pro;
- nenhuma regra de negócio muda com o modo: preflight com custo confirmado,
  isolamento por conta, ledger e verificações são idênticos nos dois; rotas
  técnicas continuam acessíveis por URL no Lite — o modo esconde, não
  bloqueia.

## Consequências

- o vocabulário leigo vive apenas na camada de UI; contratos, eventos e
  auditoria seguem com os termos canônicos;
- toda tela nova deve declarar em qual modo aparece (padrão: só Pro);
- o manual ilustrado e os textos do Lite compartilham o mesmo tom — mudanças
  de linguagem devem manter os dois alinhados;
- o modo é por conta, não por dispositivo: trocar num navegador troca em
  todos.
