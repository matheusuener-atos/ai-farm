using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

public sealed class PostgresInterfaceModeTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task NewAccountsStartInLiteAndModeRoundTripsPerAccount()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var firstIdentity = NewIdentity("mode-first");
        var secondIdentity = NewIdentity("mode-second");

        try
        {
            await using var first = BuildProvider(connectionString, firstIdentity);
            await using var second = BuildProvider(connectionString, secondIdentity);
            var firstMode = first.GetRequiredService<IInterfaceModeService>();
            var secondMode = second.GetRequiredService<IInterfaceModeService>();

            // A conta ainda nem existe: o padrão é Lite (leigos primeiro).
            Assert.Equal(AIFarmInterfaceMode.Lite, await firstMode.GetAsync());

            // A criação via wallet preserva o padrão Lite do registro novo.
            await first.GetRequiredService<IWalletService>().GetSnapshotAsync();
            Assert.Equal(AIFarmInterfaceMode.Lite, await firstMode.GetAsync());

            await firstMode.SetAsync(AIFarmInterfaceMode.Pro);
            Assert.Equal(AIFarmInterfaceMode.Pro, await firstMode.GetAsync());

            // Isolamento por conta: a segunda continua no padrão.
            Assert.Equal(AIFarmInterfaceMode.Lite, await secondMode.GetAsync());

            // Set sem conta prévia cria o registro com o modo escolhido.
            await secondMode.SetAsync(AIFarmInterfaceMode.Pro);
            Assert.Equal(AIFarmInterfaceMode.Pro, await secondMode.GetAsync());

            await firstMode.SetAsync(AIFarmInterfaceMode.Lite);
            Assert.Equal(AIFarmInterfaceMode.Lite, await firstMode.GetAsync());
            Assert.Equal(AIFarmInterfaceMode.Pro, await secondMode.GetAsync());
        }
        finally
        {
            await CleanupAsync(connectionString, firstIdentity);
            await CleanupAsync(connectionString, secondIdentity);
        }
    }

    private static AIFarmIdentity NewIdentity(string label)
    {
        var accountId = Guid.NewGuid();
        return new AIFarmIdentity(accountId, accountId, $"atos {label} test");
    }

    private static ServiceProvider BuildProvider(
        string connectionString,
        AIFarmIdentity identity)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(connectionString, identity);
        return services.BuildServiceProvider();
    }

    private static async Task CleanupAsync(
        string connectionString,
        AIFarmIdentity identity)
    {
        await using var provider = BuildProvider(connectionString, identity);
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Wallets
            .Where(x => x.Id == identity.WalletId)
            .ExecuteDeleteAsync();
        await db.Accounts
            .Where(x => x.Id == identity.AccountId)
            .ExecuteDeleteAsync();
    }
}
