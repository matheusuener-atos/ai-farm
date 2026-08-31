# A7OS AI Farm

Base inicial do produto A7OS AI Farm em .NET 10 e Blazor. O incremento atual demonstra o ciclo econômico com Fake Provider:

```text
1.000 AIC Promotional
→ reserva de 20 AIC
→ Fake Provider
→ captura de 12 AIC
→ liberação de 8 AIC
→ saldo final de 988 AIC
```

## Estrutura

- `A7OS.AIFarm.Core`: domínio, Ledger e contratos de aplicação sem dependência de UI ou EF Core.
- `A7OS.AIFarm.Infrastructure`: adapters de desenvolvimento, Fake Provider e esquema EF Core/PostgreSQL.
- `A7OS.AIFarm.Web`: Blazor Web App interativo com Início, Chat, Wallet e placeholder explícito da Farm.
- `A7OS.AIFarm.UnitTests`: invariantes e operações do Ledger.
- `A7OS.AIFarm.IntegrationTests`: ciclo completo de aplicação usando os adapters em memória.

## Executar

```powershell
dotnet restore A7OS.AIFarm.slnx
dotnet test A7OS.AIFarm.slnx
dotnet run --project src/A7OS.AIFarm.Web
```

O app usa armazenamento em memória e concede 1.000 AIC promocionais ao iniciar. O contexto PostgreSQL já está modelado, mas ainda não é o adapter ativo. Não use os valores do Fake Provider como preço real.

## Estado do Marco 1

Implementado:

- AIC em unidade inteira mínima (`1 AIC = 1.000 mAIC`), sem `float`/`double`;
- grant promocional, reserva, captura e liberação;
- lançamentos balanceados e append-only no agregado;
- idempotência de operações financeiras e requisições de Chat;
- Pricing e Router separados do Provider;
- falha e cancelamento liberam a reserva;
- UI responsiva no sistema visual terminal/ASCII.

Pendente para concluir o Marco 1:

- repository PostgreSQL transacional e migration inicial;
- execução dos testes de persistência contra PostgreSQL real;
- persistência de Conversation, Message, AiRequest e AiExecution;
- expiração automática de reservas e recuperação após restart;
- autenticação mínima e isolamento por conta;
- endpoints/erros e observabilidade.

Consulte [ADR 0001](docs/ADR/0001-ledger-experimental.md) para as decisões experimentais deste incremento.
