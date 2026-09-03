# ADR 0025 — Rotação da chave de assinatura de manifestos

## Status

Aceito em 2026-09-03.

## Contexto

A chave HMAC `AIFarm:Farm:ManifestSigningKey` assina manifestos de Jobs e sela
bundles de evidência. A verificação profunda (ADR 0013) recalculava a chave
derivada por Node a partir da única chave configurada e exigia igualdade com o
`EvidenceKeyId` persistido; trocar a chave invalidava a revalidação de todos os
Jobs históricos, zerava o CP verificado em analytics e no saldo técnico e
marcava a trilha de auditoria como inválida. Não existia procedimento de
rotação: a chave era, na prática, permanente.

A evidência já persiste o `KeyId` (16 hex, derivado da chave por Node) desde o
bundle selado (`farm_jobs.EvidenceKeyId`); o manifesto não persiste KeyId e a
seleção de chave dependia apenas de `ManifestVersion`.

## Decisão

- `FarmManifestSigningOptions` passa a aceitar, além da chave ativa, uma lista
  de chaves aposentadas (`AIFarm:Farm:RetiredManifestSigningKeys`, valores
  Base64 separados por vírgula ou ponto e vírgula); cada chave aposentada
  continua sujeita ao mínimo de 256 bits;
- assinatura e selagem usam exclusivamente a chave ativa; chaves aposentadas
  nunca assinam;
- a verificação de evidência localiza a chave candidata pelo `EvidenceKeyId`
  persistido, entre a ativa e as aposentadas; sem correspondência, a chave
  ativa mantém o comportamento fail-closed atual;
- a verificação de manifesto tenta a chave ativa e depois as aposentadas, na
  ordem configurada, porque o manifesto não persiste KeyId; o custo é limitado
  pelo tamanho do ring;
- nenhuma migration é necessária: o `EvidenceKeyId` existente é suficiente
  para identificar a chave de origem de cada bundle.

Procedimento de rotação: gerar a nova chave, movê-la para
`AIFarm__Farm__ManifestSigningKey` e acrescentar a chave anterior a
`AIFarm__Farm__RetiredManifestSigningKeys`, reiniciando o serviço.

## Consequências

- Jobs históricos permanecem profundamente verificáveis após a rotação, sem
  reassinatura e sem mutação de linhas seladas;
- Compute Agents fixam o KeyId derivado no vínculo (`ManifestKeyId` na
  configuração local) e recusam ofertas assinadas por outra chave; após a
  rotação, cada Agent exige um novo vínculo explícito — comportamento
  intencional, não um defeito;
- ofertas e trusts novos usam somente a chave ativa; a janela de convivência
  entre chaves vale apenas para revalidação de histórico;
- remover uma chave do ring volta a invalidar os Jobs selados por ela — a
  lista aposentada deve ser preservada enquanto o histórico importar;
- a chave de Development continua aceita apenas em Development e não participa
  do ring de produção.
