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
- `atos.AIFarm.Web`: Blazor Web App interativo com Início, Chat, Wallet e placeholder explícito da Farm.
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
- persistência de Conversation e Message com correlação à execução;
- recuperação do histórico do Chat após restart;
- criação, listagem e seleção de múltiplas conversas por conta;
- arquivamento não destrutivo, preservando mensagens e auditoria;
- visualização e restauração de conversas arquivadas;
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

O Marco 1 está funcionalmente completo com o Fake Provider. Integrações reais,
políticas comerciais e a Farm distribuída continuam fora deste marco.

Consulte [ADR 0001](docs/ADR/0001-ledger-experimental.md) para as decisões experimentais deste incremento.
