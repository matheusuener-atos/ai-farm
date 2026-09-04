using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

[Collection("Farm dispatch persistence")]
public sealed class PostgresFarmAgentCredentialTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task RotationKeepsTheNodeAndInvalidatesTheOldCredential()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "credential rotation test");
        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            await provider.InitializeAIFarmAsync();
            var agents = provider.GetRequiredService<IFarmAgentService>();
            var enrollment = await agents.CreateEnrollmentAsync();
            var linked = Assert.IsType<FarmAgentEnrollmentResult>(await agents.EnrollAsync(
                enrollment.Token,
                new FarmAgentEnrollmentRequest(
                    "ROTATION-PC", ".NET 10 // TEST", 4, 512L * 1024 * 1024,
                    FarmAgentProtocol.CurrentAgentVersion)));

            var before = Assert.IsType<FarmAgentIdentity>(
                await agents.GetIdentityAsync(linked.Credential));
            Assert.Equal(linked.NodeId, before.NodeId);
            Assert.Equal("ROTATION-PC", before.NodeName);
            Assert.Equal("credential rotation test", before.AccountDisplayName);
            Assert.False(before.SharePlatformWork);
            Assert.NotNull(before.CredentialIssuedAtUtc);

            var rotation = Assert.IsType<FarmAgentCredentialRotation>(
                await agents.RotateCredentialAsync(linked.Credential));
            Assert.Equal(linked.NodeId, rotation.NodeId);
            Assert.NotEqual(linked.Credential, rotation.Credential);

            // A credencial anterior morre na hora, em todas as rotas.
            Assert.Null(await agents.GetIdentityAsync(linked.Credential));
            Assert.Null(await agents.RotateCredentialAsync(linked.Credential));
            Assert.Null(await agents.HeartbeatAsync(linked.Credential, Heartbeat()));

            // A nova assume o mesmo node, sem novo vínculo.
            var after = Assert.IsType<FarmAgentIdentity>(
                await agents.GetIdentityAsync(rotation.Credential));
            Assert.Equal(linked.NodeId, after.NodeId);
            // O Postgres arredonda para microssegundos; ticks exatos divergem.
            Assert.NotNull(after.CredentialIssuedAtUtc);
            Assert.True(
                (rotation.IssuedAtUtc - after.CredentialIssuedAtUtc!.Value).Duration() <
                TimeSpan.FromMilliseconds(1));
            Assert.NotNull(await agents.HeartbeatAsync(rotation.Credential, Heartbeat()));
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task IdentityAndRotationRejectUnknownCredentials()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "credential reject test");
        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            await provider.InitializeAIFarmAsync();
            var agents = provider.GetRequiredService<IFarmAgentService>();
            Assert.Null(await agents.GetIdentityAsync("afc_desconhecida"));
            Assert.Null(await agents.RotateCredentialAsync("afc_desconhecida"));
            Assert.Null(await agents.GetIdentityAsync(""));
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    private static FarmAgentHeartbeatRequest Heartbeat() => new(
        ".NET 10 // TEST", 4, 512L * 1024 * 1024,
        FarmAgentProtocol.CurrentAgentVersion, true, 1, 256L * 1024 * 1024);

    private static ServiceProvider BuildProvider(
        string connectionString, AIFarmIdentity identity)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(connectionString, identity);
        return services.BuildServiceProvider();
    }

    private static async Task CleanupAsync(string connectionString, Guid accountId)
    {
        await using var provider = BuildProvider(connectionString, new(accountId, accountId, "cleanup"));
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.FarmAgentEnrollments.RemoveRange(db.FarmAgentEnrollments.Where(x => x.AccountId == accountId));
        db.FarmNodes.RemoveRange(db.FarmNodes.Where(x => x.AccountId == accountId));
        await db.SaveChangesAsync();
        var walletIds = await db.Wallets.Where(x => x.AccountId == accountId)
            .Select(x => x.Id).ToArrayAsync();
        var transactionIds = await db.LedgerTransactions.Where(x => walletIds.Contains(x.WalletId))
            .Select(x => x.Id).ToArrayAsync();
        db.BalanceReservations.RemoveRange(db.BalanceReservations.Where(x => walletIds.Contains(x.WalletId)));
        db.LedgerEntries.RemoveRange(db.LedgerEntries.Where(x => transactionIds.Contains(x.TransactionId)));
        await db.SaveChangesAsync();
        db.LedgerTransactions.RemoveRange(db.LedgerTransactions.Where(x => walletIds.Contains(x.WalletId)));
        await db.SaveChangesAsync();
        db.Wallets.RemoveRange(db.Wallets.Where(x => x.AccountId == accountId));
        db.Accounts.RemoveRange(db.Accounts.Where(x => x.Id == accountId));
        await db.SaveChangesAsync();
    }
}
