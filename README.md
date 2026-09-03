# atos AI Farm

Base inicial do produto atos AI Farm em .NET 10 e Blazor. O incremento atual demonstra o ciclo econômico com Fake Provider:

```text
1.000 AIC Promotional
→ reserva de 20 AIC
→ Fake Provider
→ captura de 12 AIC
→ liberação de 8 AIC
→ saldo final de 988 AIC
```

## Estrutura

- `atos.AIFarm.Core`: domínio, Ledger e contratos de aplicação sem dependência de UI ou EF Core.
- `atos.AIFarm.Infrastructure`: adapters de desenvolvimento, Fake Provider e esquema EF Core/PostgreSQL.
- `atos.AIFarm.Web`: Blazor Web App interativo com Início, Chat, Wallet, Operações e Compute Lab controlado.
- `atos.AIFarm.UnitTests`: invariantes e operações do Ledger.
- `atos.AIFarm.IntegrationTests`: ciclo completo de aplicação usando os adapters em memória.

## Executar

```powershell
dotnet restore atos.AIFarm.slnx
dotnet tool restore
dotnet test atos.AIFarm.slnx
dotnet run --project src/atos.AIFarm.Web
```

Em Development, o app carrega .env.local, usa o Neon Postgres e concede
1.000 AIC promocionais por conta de forma idempotente. DATABASE_URL é
obrigatória porque contas autenticadas, Wallet e Chat são persistidos no mesmo
PostgreSQL. Fora de Development, o arquivo .env.local nunca é carregado. Não
use os valores do Fake Provider como preço real.

## Neon

O projeto Neon usa duas branches persistentes:

- `development`: ambiente local, selecionado em `.neon` e `.env.local`;
- `production`: ambiente do Web Service no Render.

Para selecionar novamente a branch local e renovar as variáveis sem exibir
credenciais:

```powershell
neon checkout development --project-id crimson-shadow-20267299
```

- `DATABASE_URL`: conexão com pool usada pelo runtime.
- `DATABASE_URL_UNPOOLED`: conexão direta usada exclusivamente por migrations.
- `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_ENDPOINT_URL_S3` e
  `AWS_REGION`: credencial branch-scoped do bucket privado `chat-attachments`.

Jobs do Compute Lab são assinados com
`AIFarm__Farm__ManifestSigningKey`. Development usa uma chave local
explicitamente não produtiva quando a variável é omitida. No Render, configure
um segredo aleatório em Base64 com pelo menos 32 bytes antes do deploy.

O arquivo versionado `neon.ts` declara o bucket privado. Para conferir e
aplicar a infraestrutura somente na branch selecionada:

```powershell
neon config plan --branch development
neon config apply --branch development
```

Os arquivos `.env.local` e `.neon` são locais e ignorados pelo Git. Para
aplicar migrations manualmente:

```powershell
dotnet tool restore
dotnet tool run dotnet-ef database update --project src/atos.AIFarm.Infrastructure --startup-project src/atos.AIFarm.Infrastructure --context AIFarmDbContext
```

## Render

O Blueprint [`render.yaml`](render.yaml) cria o Web Service Docker a partir da
branch Git `main`, na região Ohio, e verifica `/health`. No primeiro deploy,
informe no Dashboard do Render as variáveis secretas da branch Neon
`production`:

- `DATABASE_URL`: URL pooled de `production`;
- `DATABASE_URL_UNPOOLED`: URL direta de `production`, usada nas migrations.
- `AIFarm__Farm__ManifestSigningKey`: segredo Base64 de 256 bits usado para
  assinar manifests do Compute Lab.
- `AIFarm__Farm__Reliability__SampleSize`: tamanho da janela recente (padrão `20`);
- `AIFarm__Farm__Reliability__PriorSuccesses`: sucessos iniciais conservadores (padrão `4`);
- `AIFarm__Farm__Reliability__PriorFailures`: falhas iniciais conservadoras (padrão `1`);
- `AIFarm__Farm__Reliability__ConsecutiveFailureThreshold`: falhas seguidas para
  abrir quarentena (padrão `3`);
- `AIFarm__Farm__Reliability__QuarantineMinutes`: duração da quarentena automática
  (padrão `30`).

Gere o segredo localmente e copie somente o resultado para o Environment do
Render:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Para trocar a chave, siga o procedimento de rotação descrito em
"Colocar a Iris em produção" abaixo: a chave anterior entra em
`AIFarm__Farm__RetiredManifestSigningKeys` e os Jobs históricos continuam
revalidáveis (ADR 0025).

Anexos exigem também as quatro variáveis `AWS_*` declaradas em `render.yaml`.
Antes de preenchê-las, o bucket deve ser criado explicitamente na branch Neon
`production`; até lá, o Chat permanece funcional e mostra anexos como
indisponíveis. A branch `production` não é alterada durante o desenvolvimento.

Não copie `.env.local` para o Render: ele pertence exclusivamente à branch
Neon `development`. O plano gratuito está declarado para evitar cobrança
acidental e pode ser alterado no Dashboard quando forem necessários uptime e
recursos de produção.

## Colocar a Iris em produção

O `render.yaml` declara `AIFarm__AI__Provider=OpenAI`; o boot em Production é
fail-fast e recusa iniciar com mensagem clara quando o provider é `OpenAI` e a
chave está ausente. Variáveis exatas no Environment do Render:

- `AIFarm__AI__Provider`: `OpenAI` (declarada no Blueprint);
- `OPENAI_API_KEY`: chave da API OpenAI (`sync: false`, nunca commitada);
- `DATABASE_URL` e `DATABASE_URL_UNPOOLED`: branch Neon `production`;
- `AIFarm__Farm__ManifestSigningKey`: chave ativa de assinatura (Base64,
  256 bits);
- `AIFarm__Farm__RetiredManifestSigningKeys`: chaves aposentadas pela rotação,
  separadas por vírgula (vazia até a primeira rotação);
- `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_ENDPOINT_URL_S3`,
  `AWS_REGION`: anexos do Chat; sem elas o Chat mostra anexos como
  indisponíveis e `/health` reporta `degraded`.

O catálogo de preços vive em `appsettings.json` (`AIFarm:AI:OpenAI`), com o
campo `PriceSource` documentando a origem de cada preço. Ao trocar modelo ou
preço, versione `PriceCatalogVersion`.

Execuções com provider real respeitam um teto de segurança por conta e por dia
UTC (`AIFarm__AI__DailySafetyCapMinor`, padrão 50.000 mAIC = 50 AIC), além do
orçamento opcional por conta. Ao atingir o teto, a execução é negada antes de
qualquer reserva, a negação fica registrada como execução falhada
(`DailySafetyCapReached`) e aparece no centro de incidentes.

### Rotação da `ManifestSigningKey`

1. Gere a nova chave (`RandomNumberGenerator`, 32 bytes, Base64).
2. Coloque a nova chave em `AIFarm__Farm__ManifestSigningKey` e acrescente a
   anterior a `AIFarm__Farm__RetiredManifestSigningKeys`.
3. Reinicie o serviço. Cada evidência guarda o `KeyId` da chave que a selou;
   a verificação profunda localiza a chave certa no ring, então Jobs
   históricos continuam revalidáveis (ADR 0025).
4. Compute Agents fixam o `KeyId` no vínculo e recusarão ofertas assinadas
   pela nova chave até um novo vínculo explícito em cada máquina — esse
   re-vínculo faz parte do procedimento, não é falha.
5. Nunca remova uma chave de `RetiredManifestSigningKeys` enquanto houver
   histórico selado por ela que precise de revalidação.

## Operação HTTP

- /health/live: confirma que o processo web está respondendo;
- /health/ready: confirma que as dependências, incluindo PostgreSQL, estão disponíveis;
- /health: alias de prontidão mantido para o health check atual do Render;
- X-Correlation-ID: acompanha cada requisição e também é devolvido na resposta.

Quando o cliente não envia um identificador de correlação válido, a aplicação
gera um automaticamente. Os health checks não retornam credenciais nem detalhes
internos de exceções.

## Proteção e observabilidade de IA

A rota autenticada `/operations` consolida, por conta, execuções, conversas,
unidades de entrada/saída, custo estimado, custo liquidado e AIC liberado. O
painel não exibe prompts nem respostas e permite filtrar as 50 execuções mais
recentes por Provider, modelo, conversa ou ID de operação.

As políticas podem ser sobrescritas por ambiente usando a sintaxe de
configuração do ASP.NET Core (por exemplo,
`AIFarm__Execution__ProviderTimeoutSeconds` no Render):

- `MaximumPromptCharacters`: 12.000;
- `MaximumCostPerRequestMinor`: 100.000 mAIC (100 AIC);
- `ProviderTimeoutSeconds`: 30;
- `CircuitBreakerFailureThreshold`: 3;
- `CircuitBreakerOpenSeconds`: 30.

O contexto do Chat também é controlado por ambiente:

- `AIFarm__Context__DefaultMessageCount`: 16 mensagens anteriores;
- `AIFarm__Context__MaximumMessageCount`: 32 mensagens anteriores;
- `AIFarm__Context__MaximumInputTokens`: 32.000 tokens estimados;
- `AIFarm__Context__SummaryTokenBudget`: 2.000 tokens estimados para resumo.

O preflight congela e mostra o contexto antes da reserva. Mensagens recentes
entram integralmente; as anteriores são condensadas em um resumo extrativo
local, sem segunda chamada de IA e sem cobrança oculta. Edição e regeneração
recortam o contexto antes da mensagem que originou o ramo. A Responses API
recebe a sequência explícita com truncamento automático desabilitado.

Cada conversa também possui um perfil de execução persistente com modelo,
instrução do sistema, criatividade de `0` a `2` e limite de tokens de saída.
O seletor lista somente as rotas realmente configuradas no ambiente; `auto`
mantém a escolha da melhor rota disponível. O limite de saída participa da
estimativa e da reserva antes da chamada ao Provider. Presets são privados da
conta, podem ser atualizados pelo mesmo nome, aplicados a outras conversas e
removidos. Uma ramificação herda o perfil da conversa de origem.

Anexos são enviados primeiro como rascunhos para o bucket privado da branch.
O PostgreSQL guarda apenas metadados, hash SHA-256 e a chave do objeto. O
preflight relê o objeto, verifica tamanho/hash e congela os bytes efetivamente
enviados; depois do sucesso, os metadados são vinculados à mensagem. São
aceitos até 3 arquivos, 5 MB por arquivo e 15 MB no total, com validação de
extensão, MIME e assinatura/conteúdo real. Downloads passam por endpoint
autenticado e isolamento por conta.

A classificação é registrada em cada execução. `PRIVADO` nunca pode usar uma
rota externa; `CONFIÁVEL` representa autorização explícita para o Provider
mostrado no preflight; `PADRÃO` é destinado a conteúdo não sensível.

Respostas concluídas podem ser avaliadas como `ÚTIL` ou `NÃO ÚTIL`; a segunda
opção aceita um motivo opcional de até 500 caracteres. A avaliação é privada da
conta, atualizável e removível. O painel `/operations` agrega satisfação por
execução — cópias históricas em ramificações não inflam a métrica — sem expor
prompts, respostas ou justificativas.

Conversas ativas podem ser exportadas por endpoints autenticados em Markdown
ou JSON. Os arquivos incluem mensagens, rotas, custos, configurações,
metadados de anexos e feedback, mas nunca incorporam credenciais nem os bytes
dos objetos privados. Não existem links públicos de compartilhamento.

Cancelamentos solicitados pelo usuário não contam como falha do Provider. Uma
estimativa acima do limite é rejeitada antes de reservar saldo, e timeout ou
falha liberam a reserva normalmente.

## Autenticação

A primeira versão usa ASP.NET Core Identity com cookies HTTP-only e usuários
persistidos no schema aifarm do Neon. Cadastro, login e logout são nativos do
Blazor; não existe dependência de Neon Auth ou de outro provedor externo.

- e-mail único por usuário;
- senha mínima de 8 caracteres, com maiúscula, minúscula, número e símbolo;
- bloqueio por 5 minutos após 5 tentativas inválidas;
- confirmação de e-mail desabilitada nesta primeira versão;
- Wallet, Ledger, Chat e chaves de idempotência filtrados pela conta autenticada.

As páginas de produto exigem autenticação. Health checks permanecem anônimos
para que o Render possa monitorar o serviço.

## Compute Lab — início da Fase 3

A rota autenticada /farm materializa uma prova fechada de compute controlado.
Ela registra um node de laboratório por conta, permite pausar e retomar sua
disponibilidade e executa somente workloads do catálogo versionado
`atos-workload-catalog-v1`.

O catálogo inicial contém `atos-sha256-chain-v1`, usado como benchmark, e
`atos-merkle-root-sha256-v1`, que constrói uma árvore determinística de 4.096
folhas. A seleção feita no painel é congelada no manifesto. Servidor, Agent e
worker compartilham a mesma allowlist e recusam identificador, versão,
iterações, política de verificação ou limites divergentes. Ambos usam
`DENY_ALL`, uma CPU lógica, até 256 MiB e reexecução independente no servidor.

O benchmark encadeia SHA-256 sobre um nonce aleatório, reexecuta o mesmo
manifest no verificador e só registra 1,000 CP quando os digests coincidem.
Cada novo Job congela um manifesto canônico assinado por HMAC-SHA256 com
`attempt_id`, validade, timeout, limites de CPU/memória, classificação
`STANDARD` e rede `DENY_ALL`. A assinatura e a allowlist são verificadas antes
do runtime; Jobs históricos anteriores permanecem visíveis como legados.
O PostgreSQL também impede mais de um Job ativo por conta, inclusive entre
abas ou instâncias concorrentes, e cancelamentos terminalizam a tentativa sem
emitir CP ou AIC.
No laboratório `EMBEDDED`, cada transição é persistida de forma ordenada:

    QUEUED → RUNNING → VERIFYING → COMPLETED
                                    ↘ FAILED

Este CP (Ponto de Processamento) é uma medição técnica experimental e específica dessa política; ainda
não existe conversão CP → AIC. O laboratório não concede reward, não aceita
shell, imagem de container, egress ou Jobs enviados pelo usuário. No modo
`EMBEDDED`, o hardware exibido pertence ao host onde a aplicação está
executando — localmente, a máquina de desenvolvimento; no Render, o container
do serviço. No modo `AGENT`, o inventário e o workload pertencem ao computador
vinculado.

### Vínculo experimental do Compute Agent

O painel `/farm` diferencia o laboratório `EMBEDDED` de um `AGENT` externo.
O usuário autenticado pode gerar um código de vínculo de uso único, válido por
10 minutos. Gerar outro código invalida os anteriores. Depois do vínculo, o
Agent recebe uma credencial de heartbeat que só é armazenada como SHA-256 no
PostgreSQL; a credencial em texto puro é retornada uma única vez.

O executável dedicado está no projeto `atos.AIFarm.Agent`. Na primeira
instalação, gere o código em `/farm` e execute:

```powershell
dotnet run --project src/atos.AIFarm.Agent -- enroll http://localhost:5159
```

O código é solicitado sem ecoar seu conteúdo. O Agent grava somente sua
configuração não sensível em `%LOCALAPPDATA%\atos-ai-farm\agent.json`; a
credencial fica no Windows Credential Manager. Para conferir o vínculo:

```powershell
dotnet run --project src/atos.AIFarm.Agent -- status
```

Para manter o node online, inicie o processo e deixe o terminal aberto:

```powershell
dotnet run --project src/atos.AIFarm.Agent -- run
```

O Agent envia o inventário e um heartbeat a cada 30 segundos. Durante um Job, o
protocolo `0.3.0` passa a enviar heartbeats a cada 2 segundos e renova um lease
de execução com validade de 10 segundos. Ele aplica retry com espera crescente
quando o Web Service fica indisponível e encerra com `Ctrl+C`.
HTTP sem TLS é aceito somente em localhost; servidores remotos exigem HTTPS.
Após 90 segundos sem heartbeat o painel marca o Agent como `OFFLINE`. Nenhuma
credencial ou código deve ser colocado no Git, em screenshots ou em logs.

O proprietário controla localmente se a máquina aceita novos Jobs e quanto do
hardware pode ser anunciado. A política fica no mesmo `agent.json`, é relida a
cada heartbeat e nunca pode ser ampliada pelo servidor:

```powershell
dotnet run --project src/atos.AIFarm.Agent -- pause
dotnet run --project src/atos.AIFarm.Agent -- resume
dotnet run --project src/atos.AIFarm.Agent -- limits 1 256
```

`limits` recebe CPU lógica e memória em MiB. `pause` não interrompe os Jobs
atuais: apenas drena o Agent e impede novos despachos e claims. O painel mantém o
Node online, mas mostra a política local pausada e seus limites efetivos. Esses
limites são uma política de admissão assinada; isolamento rígido de CPU/memória
no sistema operacional ainda não faz parte deste runtime experimental.

O Agent `0.4.0` separa o workload do processo coordenador. Depois de validar o
manifesto, o coordenador inicia diretamente um processo filho interno, sem
shell, e envia por `stdin` somente um envelope IPC limitado a 4 KiB com
`workload`, `nonce` e `iterations`. O worker possui uma allowlist fixa para
`SHA256_CHAIN`, devolve apenas digest e duração por `stdout` e não recebe
credenciais, URL do servidor ou chave de manifesto. Timeout, encerramento do
Agent e cancelamento remoto matam toda a árvore do worker. Limites rígidos de
CPU e memória no sistema operacional foram adicionados na versão seguinte.

O Agent `0.5.0` aplica afinidade de CPU ao processo filho. No Windows, um Job
Object impõe o teto de memória do manifesto e encerra o worker se ele o
ultrapassar; o limite também é aplicado ao heap do .NET. Em plataformas onde o
Job Object não existe, a afinidade é usada quando suportada e o coordenador
monitora o working set, encerrando a árvore ao ultrapassar o teto. O modo efetivo
fica explícito como `WINDOWS_JOB_OBJECT...` ou `PROCESS_MONITOR...`, sem fingir
que o fallback é isolamento nativo equivalente.

Ao concluir, o coordenador mede duração, tempo de CPU e pico de memória do
processo. A API aceita apenas modos de contenção conhecidos, persiste o conjunto
completo de telemetria e o painel `/farm` o exibe em cada Job. Esses valores são
observações do Agent autenticado e entram na evidência operacional do Job; não
constituem attestation independente do hardware.

A verificação do bundle não termina na assinatura HMAC. O servidor também
revalida o manifesto, confere o vínculo de todos os campos econômicos e técnicos
com o Job atual e compara, evento a evento, o lifecycle selado com a trilha
persistida. O painel expõe esses quatro diagnósticos separadamente. Qualquer
divergência invalida a evidência no Centro de Auditoria, bloqueia seu download e
impede que ela origine uma nova fotografia econômica ou proposta de reward,
mesmo quando a assinatura do payload original continua matematicamente válida.
Saldo e analytics contam CP apenas de bundles aprovados nas quatro verificações;
o painel analítico destaca separadamente as falhas de integridade.

O Agent `0.6.0` substitui a regra fixa de um único benchmark pelo catálogo
compartilhado. O IPC `atos-worker-ipc-v2` inclui workload e versão, e o worker
continua sem aceitar comandos, caminhos, URLs ou código fornecido pelo usuário.
Adicionar um workload exige uma definição versionada, implementação local
determinística e verificação equivalente no servidor.

O Agent `0.7.0` e o IPC `atos-worker-ipc-v3` introduzem **Useful Jobs**. A tela
`/farm/demands` aceita somente texto `STANDARD` de até 32 KiB e despacha o
workload `IRIS_TEXT_INDEX`. Ele normaliza o texto, conta termos, cria segmentos
de até 200 tokens e produz um índice JSON determinístico para recuperação
lexical e preparação de contexto da Iris. O digest da entrada faz parte do
manifesto assinado. A saída do Agent é reexecutada e comparada byte a byte pelo
servidor; entrada, saída e seus digests também ficam vinculados ao bundle de
evidência. Nenhum conteúdo `PRIVATE` ou `CONFIDENTIAL` é aceito nessa rota.

Esta versão persiste payloads pequenos diretamente no PostgreSQL. Ela não é um
modelo de embeddings nem promete similaridade semântica; armazenamento de
artefatos maiores no Object Storage e workloads de inferência são fases
posteriores. A conclusão válida gera 2,000 CP técnicos e continua sem converter
CP em AIC automaticamente.

O **Compute Pool v1** remove o limite histórico de um único Agent por conta.
Cada nome de máquina representa um Node independente; vincular novamente o
mesmo nome rotaciona somente sua credencial. Quando há Agents cadastrados, o
scheduler escolhe apenas entre Agents online, compatíveis, com heartbeat atual,
política local ativa e capacidade residual. Sem Agents cadastrados, o runtime
embedded continua disponível para desenvolvimento.

A alocação usa um advisory lock transacional por conta no PostgreSQL. Assim,
duas instâncias web podem receber submissões simultâneas sem exceder o mesmo
Node. Cada Job congela sua reserva de CPU e memória; o scheduler soma reservas
ativas e usa o saldo como critério de admissão. O painel `/farm` lista todo o
pool e a API permite operar cada Node separadamente.

O Agent `0.8.0` introduz **multi-slot**. Um único Agent pode manter vários
workers isolados em paralelo quando a soma dos limites assinados cabe em
`limits <cpu> <memoria-mb>`. Claim informa os Jobs já ativos, heartbeat renova
todos os leases e recebe cancelamentos individualizados. Reduzir a política
local impede novos claims, mas permite que os processos em curso terminem. A
matriz `/farm/capacity` mostra Jobs ativos e CPU/memória restantes; um Node em
uso continua aceitando trabalho enquanto possuir saldo suficiente.

A **Demand Queue v1** separa aceitação de execução. `/farm/demands` persiste a
entrada antes de procurar capacidade, aceita prioridades baixa, normal e alta,
validade entre cinco minutos e sete dias, cancelamento pré-despacho e atualização
automática do estado. Um worker tenta despachar primeiro as demandas prioritárias
e mais antigas, aplica backoff quando todos os Nodes compatíveis estão ocupados e
expira itens que não obtiveram capacidade no prazo.

O claim é atômico no PostgreSQL e suporta múltiplas instâncias web. O ID da
demanda também é gravado com unicidade no Job, tornando a retomada idempotente
mesmo se o processo cair entre a criação do Job e a confirmação da fila. A API
pessoal oferece `GET/POST /api/v1/farm/demands` e
`POST /api/v1/farm/demands/{id}/cancel`; submissões exigem
`X-Idempotency-Key`. O Centro de Jobs identifica `DEMANDA` como origem própria.

O **Resource-Aware Scheduler v1** acrescenta requisitos mínimos de CPU e memória
ao catálogo versionado. Ao aceitar uma demanda, a Farm congela a versão e os
recursos exigidos no PostgreSQL; uma mudança posterior do catálogo não pode
alterar silenciosamente o contrato que já estava na fila. Antes de criar o
manifesto, o scheduler elimina Nodes pausados, offline, com heartbeat vencido,
política local desabilitada, Agent incompatível, recursos insuficientes ou Job
ativo. Entre os elegíveis, usa best-fit por CPU e memória e preserva máquinas
maiores para workloads que realmente precisem delas.

`/farm/capacity` apresenta a matriz em tempo real de Node × workload, incluindo
CPU/memória autorizadas, ocupação e o motivo exato de cada bloqueio. A mesma
visão está disponível em `GET /api/v1/farm/capacity` com escopo de leitura da
Farm. A fila mostra o contrato de recursos congelado em cada demanda e utiliza
os diagnósticos do scheduler nas retentativas.

O **Reliability-Aware Scheduler v1** calcula um score reproduzível a partir da
janela recente de Jobs de cada Node. Conclusões elevam a confiança; falhas,
timeouts e rejeições a reduzem; cancelamentos não penalizam a máquina. Três
falhas consecutivas abrem, por padrão, quarentena de 30 minutos, impedindo novos
Jobs sem ocultar ou pausar manualmente o Node. A expiração é automática.

Entre Nodes elegíveis, a maior confiabilidade precede o best-fit de recursos. A
matriz e `GET /api/v1/farm/capacity` expõem score, faixa, amostra, sequência de
falhas e retorno previsto. Quarentenas ativas também geram incidente crítico.
O cálculo usa o histórico existente e não requer migration.

A **Distributed Demand v1** permite que uma única entrada de texto seja
processada por até oito Nodes. Ao aceitar a demanda, o servidor divide tokens
contíguos em shards determinísticos e persiste cada parte com digest próprio.
O dispatcher cria um Job útil por shard, sempre respeitando o limite de um Job
ativo por Node; se a capacidade acabar no meio do fan-out, preserva os Jobs já
criados e retoma somente as partes restantes. Quedas entre criação e vínculo
também são recuperadas pelo par único demanda + sequência.

Cada Agent recebe apenas seu shard no protocolo já existente. O resultado é
reexecutado e sua evidência é validada individualmente. Somente quando todos os
shards terminam aprovados o coordenador ordena as partes, soma as métricas e
grava um `atos-distributed-text-index-v1` canônico com SHA-256. A tela mostra o
progresso por shard e libera o JSON consolidado; a API expõe
`GET /api/v1/farm/demands/{id}/output` com `FarmRead`. Cada Job aprovado mantém
seu CP técnico conforme o catálogo, mas este fan-in não cria reward extra nem
define conversão CP → AIC.

O **Shard Retry & Failover v1** permite congelar de uma a cinco tentativas por
shard, com três por padrão. Falha, timeout, rejeição ou cancelamento terminal
acionam backoff exponencial e uma nova tentativa que evita o Node anterior. Os
shards já aprovados não são refeitos, e a consolidação considera somente a
tentativa vencedora de cada parte.

Cada tentativa permanece como Job imutável, com Node, status, falha e evidência
próprios. A fila mostra a trilha completa e sinaliza failover pendente. Se não
houver outro Node elegível, a demanda continua em processamento até surgir
capacidade ou vencer sua validade; ao esgotar o orçamento persistido, termina
como falha sem apagar os resultados parciais.

O Agent `0.2.0` introduz o perfil observado `atos-node-profile-v1`. Runtime,
versão do Agent, CPU lógica e memória detectada formam um fingerprint SHA-256
que aparece abreviado tanto em `atos-agent status` quanto no painel. O manifesto
remoto v3 congela e assina esse fingerprint. Depois que a evidência
determinística é aprovada, o perfil recebe data de verificação; qualquer mudança
em um de seus componentes revoga o selo e volta o Node para `DETECTADO` até um
novo workload verificado. Isso comprova somente a combinação observada que executou o
workload — não é attestation criptográfica de fabricante, TPM ou GPU.

Com o Agent online, a ação de despachar o workload selecionado cria uma oferta remota. O
servidor assina o manifesto com uma chave HMAC derivada exclusivamente para o
Node; o Agent fixa a identidade dessa chave no Windows Credential Manager,
confere assinatura, validade, vínculo, workload e limites da allowlist antes de
executar. O Agent devolve somente o digest e a duração, e o servidor reexecuta o
workload para aceitar ou rejeitar a evidência:

    QUEUED → OFFERED → ACCEPTED → RUNNING → UPLOADING → VERIFYING → COMPLETED
       │         │          │          │          │          │
       └─────────┴──────────┴──────────┴──────────┴──────────┴→ FAILED
                                  ├→ CANCELLED
                                  ├→ TIMED_OUT
                                  └→ REJECTED

Falhas de assinatura, manifesto expirado, resultado divergente ou estado
inválido encerram a tentativa sem emitir CP ou AIC. Reenvios idempotentes só são
aceitos quando apresentam exatamente a mesma evidência já concluída. O watchdog
terminaliza ofertas abandonadas ao fim da validade assinada e runtimes que
ultrapassam 30 segundos, liberando o índice de Job ativo para um novo despacho.
Enquanto a oferta estiver em `OFFERED`, `ACCEPTED` ou `RUNNING`, o usuário pode
cancelá-la no painel. Uma oferta ainda não aceita é encerrada imediatamente; em
um Job já aceito, o Agent recebe o aviso no próximo heartbeat ativo (1–10 s) e
mata a árvore do worker via Job Object, e o watchdog encerra pelo servidor se a
confirmação ou o lease não chegar. A partir do pedido de cancelamento nenhum
upload de evidência é aceito: o Job termina `Cancelled` com resultado,
telemetria e saída tipada descartados (ADR 0027). O painel atualiza esses
estados automaticamente a cada 2 segundos.

## Estado do Marco 1

Implementado:

- AIC em unidade inteira mínima (`1 AIC = 1.000 mAIC`), sem `float`/`double`;
- grant promocional, reserva, captura e liberação;
- lançamentos balanceados e append-only no agregado;
- idempotência de operações financeiras e requisições de Chat;
- Pricing e Router separados do Provider;
- falha e cancelamento liberam a reserva;
- repository PostgreSQL transacional e migrations aplicadas no Neon;
- persistência idempotente de AiRequest e AiExecution;
- medição persistida de unidades de entrada/saída e duração da execução;
- limite configurável de prompt e custo máximo por tarefa antes da reserva;
- timeout e circuit breaker para isolar falhas repetidas do Provider;
- painel operacional por conta, sem revelar prompts ou respostas;
- persistência de Conversation e Message com correlação à execução;
- recuperação do histórico do Chat após restart;
- criação, listagem e seleção de múltiplas conversas por conta;
- arquivamento não destrutivo, preservando mensagens e auditoria;
- visualização e restauração de conversas arquivadas;
- título automático derivado da primeira mensagem da conversa;
- renomeação manual de conversas, com normalização e isolamento por conta;
- acesso a conversas validado antes de qualquer reserva no Ledger;
- preflight do Chat com rota, catálogo e limite em AIC antes da reserva;
- confirmação explícita antes de executar o Provider e alterar o Ledger;
- teste de persistência real do Chat contra PostgreSQL;
- concorrência de reservas serializada por Wallet no PostgreSQL;
- recuperação de saldo disponível e reservado após restart;
- expiração auditável de reservas, recuperada no startup e verificada a cada 30 segundos;
- rejeição de captura tardia após o vencimento da reserva;
- health checks separados para processo vivo e dependências prontas;
- correlação de requisições HTTP sem registrar prompts ou corpos;
- cadastro, login e logout com ASP.NET Core Identity;
- Wallet, Ledger e Chat isolados pela conta autenticada;
- UI responsiva no sistema visual terminal/ASCII.
- contexto inteligente com limite de mensagens configurável por execução;
- estimativa preventiva de tokens e limite seguro de entrada;
- resumo extrativo automático para histórico omitido;
- inspeção, no preflight, do contexto efetivamente enviado ao Provider;
- contexto de ramificações isolado da continuação original.
- configuração persistente de modelo, instrução, criatividade e saída por conversa;
- presets reutilizáveis e isolados por conta;
- perfil de execução congelado no preflight e herdado por ramificações;
- parâmetros `instructions`, `temperature` e `max_output_tokens` enviados à Responses API.
- anexos privados branch-scoped no Neon Object Storage, com metadados no PostgreSQL;
- verificação de integridade SHA-256, tipo real e limites antes do preflight;
- payload multimodal oficial (`input_image`, `input_file` e `input_text`);
- classificação auditável e bloqueio de dados privados em Providers externos;
- download autenticado e preservação das referências em ramificações.
- feedback persistente e atualizável por resposta, isolado por conta;
- satisfação agregada por execução no painel operacional;
- exportação autenticada e portável de conversas em Markdown e JSON.
- Compute Lab persistente com node por conta, pausa/retomada, benchmark determinístico, CP técnico e trilha de estados.
- protocolo inicial de Compute Agent com vínculo único, credencial rotacionável, heartbeat e detecção de offline.
- executável Windows do Compute Agent com inventário, Credential Manager, heartbeat contínuo, retry e desligamento controlado.
- score determinístico de confiabilidade por Node, calculado em lote sobre o histórico recente;
- quarentena automática após falhas consecutivas, com expiração, diagnóstico de capacidade e incidente crítico;
- seleção de capacidade priorizada por confiabilidade antes do best-fit de CPU e memória.
- despacho remoto assinado por Node, execução allowlisted no Agent e verificação determinística independente no servidor.
- cancelamento remoto idempotente, rejeição explícita e watchdog de ofertas/runtimes abandonados.
- política local persistente do Provider com pausa graciosa e limites anunciados de CPU/memória.
- fingerprint assinado do perfil observado, verificação datada e revogação automática após mudanças.
- propostas imutáveis de reward vinculadas a Job, evidência e cenário econômico, sem emissão automática de AIC.
- Centro de Auditoria unificado e isolado por conta para Ledger, Iris, Farm, economia e rewards, com filtros, validação de integridade e exportação JSON digestada.
- Centro de Incidentes que prioriza falhas de integridade, Iris, Farm e Node, com reconhecimento persistente e idempotente sem alterar a evidência original.
- orçamento preventivo da Iris por conta, com teto por tarefa, dia e mês UTC, retenções concorrentes idempotentes e painel em `/iris/budget`.
- tokens pessoais da Iris com segredo exibido uma vez, hash SHA-256, escopos mínimos, expiração, revogação, rate limit e endpoints REST `/api/v1` para consulta e execução idempotente.
- Centro Analítico da Iris em `/iris/analytics`, com séries UTC, sucesso, latência, unidades, custos, economia de reserva, comparação entre períodos e breakdown por rota sem expor prompts ou respostas.
- Centro Analítico da Farm em `/farm/analytics`, com CP verificado, vazão, sucesso, falhas, workloads, saúde do Node/Agent e pipeline econômico não liquidado.
- catálogo compartilhado de workloads com seleção na Farm, benchmark SHA-256 e construção verificável de raiz Merkle no worker isolado.
- API pessoal de Jobs da Farm com escopo privilegiado independente, submissão idempotente, consulta, cancelamento cooperativo e download de evidência assinada.
- verificação profunda de evidências contra manifesto, Job e lifecycle persistido, integrada à UI, API, Auditoria, downloads e gate de rewards.
- Centro de Jobs em `/farm/jobs`, com histórico paginado, filtros operacionais, origem API/painel, inspeção do lifecycle e exportação JSON digestada.
- Centro de Agendamentos em `/farm/schedules`, com recorrência UTC persistente, pausa/retomada, despacho idempotente entre instâncias e histórico de ocorrências.
- retentativa persistente por shard com backoff exponencial, limite congelado e histórico auditável.
- failover automático que evita o último Node falho e preserva shards já aprovados.

### API pessoal da Iris

Emita e revogue credenciais na rota autenticada `/account/api-tokens`. O segredo
`iris_pat_...` aparece uma única vez e deve ser enviado como Bearer somente aos
endpoints compatíveis com os escopos selecionados:

```text
GET /api/v1/me
GET /api/v1/usage
GET /api/v1/usage/analytics?days=30
GET /api/v1/incidents?openOnly=true
GET /api/v1/audit?page=1&pageSize=25
GET /api/v1/conversations?page=1&pageSize=25
GET /api/v1/conversations/{conversationId}
GET /api/v1/farm/analytics?days=30
GET /api/v1/farm/workloads
GET /api/v1/farm/capacity
GET /api/v1/farm/demands
POST /api/v1/farm/demands
GET /api/v1/farm/demands/{demandId}/output
GET /api/v1/farm/schedules
GET /api/v1/farm/jobs?page=1&pageSize=20
GET /api/v1/farm/jobs/export
POST /api/v1/farm/jobs
POST /api/v1/farm/schedules
POST /api/v1/farm/schedules/{scheduleId}/pause
POST /api/v1/farm/schedules/{scheduleId}/resume
POST /api/v1/farm/schedules/{scheduleId}/trigger
GET /api/v1/farm/jobs/{jobId}
POST /api/v1/farm/jobs/{jobId}/cancel
GET /api/v1/farm/jobs/{jobId}/evidence
POST /api/v1/iris/responses
```

Exemplo, sem registrar o token no repositório:

```powershell
$headers = @{ Authorization = "Bearer $env:IRIS_API_TOKEN" }
Invoke-RestMethod "http://localhost:5159/api/v1/usage" -Headers $headers
```

Para executar a Iris, emita um token com `IrisExecute`. Esse escopo pode
consumir AIC e, portanto, deve ser concedido somente a clientes autorizados. A
rota exige uma chave de idempotência entre 8 e 120 caracteres, isolada por
token. Repetir a mesma chave devolve o resultado anterior sem nova chamada ao
Provider, nova cobrança ou nova conversa:

```powershell
$headers = @{
  Authorization = "Bearer $env:IRIS_API_TOKEN"
  "X-Idempotency-Key" = "meu-job-0001"
}
$body = @{
  prompt = "Resuma os incidentes operacionais desta semana"
  maximumContextMessages = 16
  dataClassification = "Standard"
} | ConvertTo-Json
Invoke-RestMethod "http://localhost:5159/api/v1/iris/responses" `
  -Method Post -ContentType "application/json" -Headers $headers -Body $body
```

Opcionalmente, envie `conversationId` para continuar uma conversa pertencente
à mesma conta. A execução usa o mesmo preflight do chat, respeita o limite por
tarefa, o orçamento diário/mensal e a classificação de dados, e reserva e
liquida o custo na Wallet. `ConversationsRead` é um escopo separado e permite
consultar o histórico sem conceder execução. As consultas têm limite de 120
chamadas por minuto por token; a rota de execução usa o limite mais restritivo
de 20 por minuto. Não registre tokens, prompts ou respostas em logs.

### API de Jobs da Farm

O escopo `FarmRead` permite consultar analytics, catálogo, Jobs e evidências da
própria conta. Para submeter ou cancelar Jobs, emita um token com o escopo
privilegiado `FarmExecute`. A API aceita somente workloads versionados do
catálogo assinado; ela não executa comandos arbitrários, não emite AIC e não
converte automaticamente CP em AIC.

A submissão exige `X-Idempotency-Key` entre 8 e 120 caracteres. A chave é
persistida e isolada por token: chamadas simultâneas com a mesma chave e o
mesmo workload devolvem o mesmo Job, sem duplicar processamento. Reutilizar a
chave para outro workload é rejeitado com conflito.

```powershell
$headers = @{
  Authorization = "Bearer $env:IRIS_API_TOKEN"
  "X-Idempotency-Key" = "farm-job-0001"
}
$body = @{ workloadId = "atos-sha256-chain-v1" } | ConvertTo-Json
$job = Invoke-RestMethod "http://localhost:5159/api/v1/farm/jobs" `
  -Method Post -ContentType "application/json" -Headers $headers -Body $body

Invoke-RestMethod "http://localhost:5159/api/v1/farm/jobs/$($job.id)" -Headers $headers
Invoke-WebRequest "http://localhost:5159/api/v1/farm/jobs/$($job.id)/evidence" `
  -Headers $headers -OutFile "farm-evidence.json"
```

`POST /api/v1/farm/jobs/{jobId}/cancel` solicita cancelamento cooperativo ao
Agent quando o Job já está em execução. Consultas exigem `FarmRead`; submissão
e cancelamento exigem `FarmExecute` e têm limite de 20 chamadas por minuto por
token. Todos os acessos são filtrados pela conta vinculada ao token. A consulta
do Job devolve `evidence.verification` com os estados independentes de HMAC,
manifesto, vínculo do Job e lifecycle; `evidence.isValid` só é verdadeiro quando
as quatro verificações são aprovadas.

### Centro de Jobs da Farm

A rota autenticada `/farm/jobs` remove a limitação operacional dos dez Jobs
recentes do Compute Lab. Ela permite pesquisar por UUID, attempt, workload,
versão ou política e filtrar por workload catalogado, status, origem, período
UTC e falha de integridade. Cada resultado inclui manifesto, diagnóstico do
bundle e lifecycle completo, sempre isolado pela conta autenticada.

O botão de exportação gera um envelope `atos-farm-job-history-export-v1` com os
filtros aplicados, totais, Jobs e digest SHA-256 do payload. Segredos de token e
chaves de idempotência não fazem parte do contrato. A consulta limita cada
varredura a 5.000 registros e sinaliza truncamento para que o operador refine o
período. Clientes com `FarmRead` usam as mesmas projeções em
`GET /api/v1/farm/jobs` e `GET /api/v1/farm/jobs/export`; paginação aceita de 1
a 100 itens por página.

### Centro de Agendamentos da Farm

A rota autenticada `/farm/schedules` automatiza workloads do catálogo em
intervalos fixos de 5 minutos a 7 dias. O primeiro disparo é imediato; cada
ocorrência é persistida, recebe atribuição ao agendamento e pode ser
`Submitted`, `Deferred` ou `Failed`. Pausar e retomar preservam o histórico.

O dispatcher consulta o relógio UTC a cada 15 segundos. A reivindicação da
ocorrência e o avanço de `NextRunAtUtc` são transacionais, com índices únicos
para impedir duplicidade mesmo com mais de uma instância. Janelas perdidas são
saltadas sem rajadas. O Job segue pelo mesmo manifesto assinado, execução
allowlisted e verificação profunda do Compute Lab; somente evidência válida
contribui CP e o agendamento nunca emite AIC.

Tokens com `FarmRead` consultam `GET /api/v1/farm/schedules`. Criação,
pausa, retomada e disparo imediato exigem `FarmExecute` e usam, respectivamente,
`POST /api/v1/farm/schedules`,
`POST /api/v1/farm/schedules/{id}/pause`,
`POST /api/v1/farm/schedules/{id}/resume` e
`POST /api/v1/farm/schedules/{id}/trigger`.

### Centro Analítico da Iris

A rota autenticada `/iris/analytics` consolida janelas móveis de 7, 30 ou 90
dias em UTC. O painel exibe execuções, taxa de sucesso, custo estimado e real,
AIC liberado, latência média, variação contra o período anterior, série diária,
distribuição de resultados e desempenho por Provider/modelo. Os dados são
calculados a partir de `AiRequest` e `AiExecution`, sempre filtrados pela conta;
prompts e respostas não participam da projeção.

Clientes com `UsageRead` obtêm o mesmo contrato por
`GET /api/v1/usage/analytics?days=30`. O parâmetro aceita de 1 a 365 dias. A
resposta preenche inclusive dias sem atividade, facilitando gráficos externos,
e inclui o período anterior apenas como base agregada de comparação.

### Centro Analítico da Farm

A rota autenticada `/farm/analytics` consolida janelas móveis de 7, 30 ou 90
dias em UTC. O painel mede Jobs ativos, concluídos e falhos, taxa de sucesso,
CP verificado, CP por hora, duração, memória, tempo de CPU, variação contra o
período anterior, série diária e desempenho por workload. Também mostra o
estado efetivo do Node/Agent e resume cenários e propostas econômicas.

Somente Jobs concluídos contribuem CP. O painel não executa workloads, não
converte CP em AIC e não liquida propostas. Todas as consultas são filtradas
pela conta autenticada. Tokens pessoais com o escopo `FarmRead` podem obter o
mesmo contrato por `GET /api/v1/farm/analytics?days=30`; o parâmetro aceita de
1 a 365 dias. O mesmo escopo consulta o catálogo público da conta em
`GET /api/v1/farm/workloads`; esse endpoint não executa Jobs.

O Marco 1 está funcionalmente completo com o Fake Provider. A primeira
integração real está implementada no Marco 2 abaixo; sua prova com orçamento
controlado depende da configuração de uma chave. Políticas comerciais e a Farm
distribuída continuam fora deste marco.

## Fase 4 — Simulador econômico

A rota autenticada `/farm/economy` inicia a prova econômica sem alterar a
Wallet. O cenário combina capacidade em CP/h, utilização, taxa de sucesso,
energia, valor bruto do workload, verificação, plataforma, impostos, margem e
reserva de risco. O resultado mostra o reward máximo financiável em BRL e o
eventual déficit. A capacidade pode ser preenchida pela vazão do último
benchmark concluído, mantendo a referência ao Job e à validade da evidência.
Ponto de equilíbrio e cenários de stress, base e favorável expõem a
sensibilidade das hipóteses. Fotografias de cenário são append-only, privadas
por conta e persistem entrada, resultado, versão, benchmark de origem e digest
SHA-256 para detectar alterações. Cada fotografia íntegra pode ser baixada como
relatório JSON pela própria conta. O envelope inclui um segundo digest SHA-256
do payload exportado; ele permite verificar integridade e não representa uma
assinatura criptográfica.

A rota autenticada `/farm/rewards` fecha a cadeia de proveniência e, quando uma
política financiada está habilitada, liquida a proposta em AIC. Antes da
liquidação, a aplicação revalida proposta, cenário, Job, manifesto e evidência.
O settlement é atômico, único por proposta e registra a partida dobrada no
Ledger: débito no bucket `Earned` da Wallet e crédito na conta versionada do
Reward Pool. Repetir a mesma solicitação devolve o settlement existente.

A política permanece desativada por padrão. Para uma homologação controlada,
configure valores inteiros em mAIC (1 AIC = 1.000 mAIC):

```dotenv
AIFarm__Farm__Rewards__Enabled=true
AIFarm__Farm__Rewards__PolicyVersion=atos-reward-policy-2026-09-v1
AIFarm__Farm__Rewards__AicMinorPerThousandCp=1000
AIFarm__Farm__Rewards__MonthlyBudgetMinor=100000
AIFarm__Farm__Rewards__MaximumPerSettlementMinor=10000
```

Nesse exemplo, 1.000 CP rendem 1 AIC, o pool mensal possui 100 AIC e cada
settlement está limitado a 10 AIC. Esses números são apenas parâmetros de
homologação, não preço ou recomendação comercial. O orçamento é global por
versão e mês UTC; não há pagamento parcial quando o saldo é insuficiente.
Qualquer alteração de taxa, teto ou orçamento deve usar uma nova
`PolicyVersion`. Tokens pessoais consultam o pool em
`GET /api/v1/farm/rewards/pool` e liquidam uma proposta própria com escopo
`FarmExecute` em `POST /api/v1/farm/rewards/{proposalId}/settle`.

Os valores iniciais são apenas um exemplo editável. O **simulador**
(`/farm/economy`) não define preço real, não converte CP em AIC e não registra
Reward no Ledger — suas fotografias são referência em BRL. A conversão
CP → AIC **existe**, mas somente pelo settlement do ADR 0022 descrito acima:
política versionada e financiada, desligada por padrão, com orçamento mensal e
partida dobrada no Ledger. Sem a política habilitada, nenhum caminho converte
CP em AIC. Escala do AIC, ordem de consumo dos buckets e modelo contábil de
compra estão propostos no ADR 0026, aguardando aprovação.

### Saldo da Farm

A rota autenticada `/saldo-farm` relaciona, sem conversão própria, todo CP de
Jobs concluídos com transações `Reward` e `Capture` do Ledger. Ela distingue
AIC gerado, AIC consumido, `Earned` disponível e saldo total. Com a política
de rewards desligada (padrão), compute verificado aparece com zero AIC gerado
de forma explícita; com a política habilitada, o AIC gerado reflete os
settlements do ADR 0022.

### Crédito administrativo no bucket Purchased

Para testar a economia com crédito não promocional, o escopo administrativo
`WalletCredit` autoriza `POST /api/v1/wallet/credits` com corpo
`{ "amountMinor": ..., "correlationId": "..." }`. O crédito é idempotente por
`CorrelationId` (repetir devolve a mesma transação; reutilizar a correlação
com valor diferente é rejeitado), limitado a 1.000 AIC por requisição,
registra partida dobrada no Ledger (débito no bucket `Purchased` da Wallet,
crédito em `atos:PURCHASED_POOL:ADMIN:v1`) e aparece na categoria Ledger do
Centro de Auditoria. Não há integração com gateway de pagamento. Enquanto o
ADR 0026 não for aprovado, o saldo `Purchased` é visível em `/wallet` mas não
é consumível — reservas continuam sacando somente de `Promotional`.

### Pool de computação da plataforma

Com `AIFarm__Farm__PlatformAccountId` configurado, as demandas úteis dessa
única conta podem ser despachadas para Compute Agents de outras contas cujo
dono fez opt-in explícito por node ("Compartilhar com a plataforma" em
`/farm`, "Ajudar a plataforma" em `/meu-computador`, ou
`POST /api/v1/farm/nodes/{id}/share`). O Job resultante pertence ao dono do
node: CP verificado, evidência e settlement seguem os fluxos normais do
trabalhador, enquanto a plataforma consolida e baixa a saída da demanda.
Somente workloads do catálogo fechado participam, o conteúdo da demanda é
visível a quem a executa (submeta apenas conteúdo não sensível) e, sem a
variável configurada, nenhum trabalho cruza fronteira de conta (ADR 0031).

## Marco 2 — Provider OpenAI

A primeira integração real usa a Responses API server-side, com o snapshot
`gpt-5.4-mini-2026-03-17`. Fake continua sendo o padrão para desenvolvimento e
testes sem custo. Para ativar localmente, acrescente a `.env.local`:

```dotenv
AIFarm__AI__Provider=OpenAI
OPENAI_API_KEY=adicione-a-chave-do-projeto-aqui
```

No Render, crie as mesmas duas variáveis em **Environment** somente quando for
executar o teste controlado. Nunca coloque a chave em `appsettings.json`, no
Git ou em componentes `.razor`.

O catálogo `openai-gpt-5.4-mini-2026-03-17-aic-v1` fixa modelo, teto de 512
tokens de saída configuráveis por conversa e tarifas internas em mAIC por milhão de tokens. Essas tarifas
são experimentais e não estabelecem paridade pública entre AIC e USD. Alterar
tarifa ou modelo exige uma nova versão de catálogo.

A resposta é criada com `store=false`. A aplicação captura a medição retornada
pela API, calcula o custo real pelo catálogo ativo e registra sucessos, timeouts,
cancelamentos e falhas no painel `/operations`, sem copiar prompts ou respostas
para logs de erro.

Consulte [ADR 0001](docs/ADR/0001-ledger-experimental.md) para as decisões experimentais deste incremento.
