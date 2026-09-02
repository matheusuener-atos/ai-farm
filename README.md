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

Gere o segredo localmente e copie somente o resultado para o Environment do
Render:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Não troque a chave sem um procedimento de rotação: Jobs históricos assinados
pela chave anterior continuarão registrados, mas não poderão ser revalidados
com a nova chave.

Anexos exigem também as quatro variáveis `AWS_*` declaradas em `render.yaml`.
Antes de preenchê-las, o bucket deve ser criado explicitamente na branch Neon
`production`; até lá, o Chat permanece funcional e mostra anexos como
indisponíveis. A branch `production` não é alterada durante o desenvolvimento.

Não copie `.env.local` para o Render: ele pertence exclusivamente à branch
Neon `development`. O plano gratuito está declarado para evitar cobrança
acidental e pode ser alterado no Dashboard quando forem necessários uptime e
recursos de produção.

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

A rota autenticada /farm agora materializa a primeira prova fechada de
compute controlado. Ela registra um node de laboratório por conta, permite
pausar e retomar sua disponibilidade e executa somente o workload versionado
atos-sha256-chain-v1.

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
do serviço. No modo `AGENT`, o inventário e o benchmark pertencem ao computador
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

`limits` recebe CPU lógica e memória em MiB. `pause` não interrompe o Job
atual: apenas drena o Agent e impede novos despachos e claims. O painel mantém o
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

O Agent `0.2.0` introduz o perfil observado `atos-node-profile-v1`. Runtime,
versão do Agent, CPU lógica e memória detectada formam um fingerprint SHA-256
que aparece abreviado tanto em `atos-agent status` quanto no painel. O manifesto
remoto v3 congela e assina esse fingerprint. Depois que a evidência
determinística é aprovada, o perfil recebe data de verificação; qualquer mudança
em um de seus componentes revoga o selo e volta o Node para `DETECTADO` até um
novo benchmark. Isso comprova somente a combinação observada que executou o
workload — não é attestation criptográfica de fabricante, TPM ou GPU.

Com o Agent online, `[ DESPACHAR BENCHMARK ]` cria uma oferta remota. O
servidor assina o manifesto com uma chave HMAC derivada exclusivamente para o
Node; o Agent fixa a identidade dessa chave no Windows Credential Manager,
confere assinatura, validade, vínculo, workload e limites da allowlist antes de
executar. O Agent devolve somente o digest e a duração, e o servidor reexecuta o
benchmark para aceitar ou rejeitar a evidência:

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
cancelá-la no painel. Uma oferta ainda não aceita é encerrada imediatamente; um
Job já aceito recebe um pedido cooperativo, permanece ativo até o Agent
confirmar o cancelamento e é encerrado pelo watchdog se a confirmação ou o
lease não chegar. O painel atualiza esses estados automaticamente a cada 2
segundos, e a transição condicional impede corrida com o envio da evidência.

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
1 a 365 dias.

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

A rota autenticada `/farm/rewards` fecha a cadeia de proveniência antes de
qualquer pagamento. Uma proposta só pode ser registrada quando o Job concluído
possui evidência HMAC válida e a fotografia econômica íntegra foi criada a
partir desse mesmo Job. Ela congela CP, valor recomendado em BRL, digests das
duas fontes, versão, horário e digest próprio. A operação é idempotente por
cenário e isolada por conta.

Todas as propostas permanecem em `AwaitingPolicyAndFunding`. Elas não criam
AIC, não movimentam o Ledger e não afirmam uma taxa CP → AIC. Liquidação só
poderá ser adicionada depois que fonte do Reward Pool, orçamento, autoridade de
aprovação e política de conversão forem definidos explicitamente.

Os valores iniciais são apenas um exemplo editável. O simulador não define
preço real, não converte CP em AIC e não registra Reward no Ledger. Uma futura
política comercial somente poderá consumir seus resultados depois que fonte de
receita, regra CP → AIC e orçamento forem decididos explicitamente.

### Saldo da Farm

A rota autenticada `/saldo-farm` relaciona, sem converter automaticamente,
todo CP de Jobs concluídos com transações `Reward` e `Capture` do Ledger. Ela
distingue AIC gerado, AIC consumido, `Earned` disponível e saldo total. Enquanto
não existir Reward Pool financiado, compute verificado aparece com zero AIC
gerado de forma explícita.

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
