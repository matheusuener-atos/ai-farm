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
- `atos.AIFarm.Web`: Blazor Web App interativo com Início, Chat, Wallet, Operações e placeholder explícito da Farm.
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

O Marco 1 está funcionalmente completo com o Fake Provider. A primeira
integração real está implementada no Marco 2 abaixo; sua prova com orçamento
controlado depende da configuração de uma chave. Políticas comerciais e a Farm
distribuída continuam fora deste marco.

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
