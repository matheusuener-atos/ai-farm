# ADR 0004 — Centro de Auditoria unificado e somente leitura

- Status: aceito
- Data: 2026-09-02

## Contexto

Ledger, execuções da Iris, Jobs da Farm, cenários econômicos e propostas de
recompensa já possuem trilhas próprias. A verificação operacional exigia
navegar por várias telas e não havia um artefato único para inspeção externa.

## Decisão

Criar uma projeção de auditoria somente leitura, sem nova tabela e sem copiar
conteúdo de prompts ou respostas. Cada consulta é limitada à conta autenticada
antes de qualquer agregação e pode ser filtrada por texto, categoria e período
UTC.

A projeção valida:

- igualdade entre débitos e créditos de cada transação do Ledger;
- assinatura HMAC e digest das evidências de Jobs concluídos;
- digest das fotografias econômicas;
- digest das propostas de recompensa.

A exportação JSON inclui versão, conta, filtros, totais, eventos e um digest
SHA-256 do payload canônico. O digest prova integridade do arquivo, mas não é
uma assinatura nem substitui as assinaturas HMAC das evidências da Farm.

## Consequências

O Centro de Auditoria não altera registros, não liquida recompensas e não cria
uma política CP → AIC. Para proteger o banco e tornar limites explícitos, uma
consulta examina no máximo 5.000 eventos por categoria e informa quando houve
truncamento; o usuário deve reduzir o período antes de exportar nesses casos.

Prompts, respostas, chaves, credenciais e payloads assinados não são incluídos
na linha do tempo nem no arquivo exportado.
