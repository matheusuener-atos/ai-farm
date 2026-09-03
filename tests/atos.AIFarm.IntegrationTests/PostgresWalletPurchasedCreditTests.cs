using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

public sealed class PostgresWalletPurchasedCreditTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task AdministrativeCreditIsIdempotentPerCorrelationAndVisibleInSnapshot()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var identity = new AIFarmIdentity(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "atos purchased credit test");
        var correlationId = Guid.NewGuid();

        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            var wallet = provider.GetRequiredService<IWalletService>();

            var first = await wallet.GrantPurchasedAsync(Aic.FromAic(50), correlationId);
            var replay = await wallet.GrantPurchasedAsync(Aic.FromAic(50), correlationId);

            Assert.False(first.WasReplayed);
            Assert.True(replay.WasReplayed);
            Assert.Equal(first.TransactionId, replay.TransactionId);
            Assert.Equal(correlationId, replay.CorrelationId);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                wallet.GrantPurchasedAsync(Aic.FromAic(60), correlationId));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                wallet.GrantPurchasedAsync(
                    WalletLimits.MaximumAdministrativeCreditMinor + 1,
                    Guid.NewGuid()));

            var snapshot = await wallet.GetSnapshotAsync();
            Assert.Equal(Aic.FromAic(50), snapshot.Balance.AvailableByBucket[CreditBucket.Purchased]);
            Assert.Equal(Aic.FromAic(50), snapshot.Balance.AvailableMinor);
            Assert.Empty(snapshot.OpenReservations);
            var grant = Assert.Single(snapshot.Transactions);
            Assert.Equal(LedgerTransactionType.Grant, grant.Type);
            Assert.Equal(correlationId, grant.CorrelationId);
            Assert.Equal(0, grant.Entries.Sum(x => x.SignedAmountMinor));
            Assert.Contains(grant.Entries, x => x.AccountCode == "atos:PURCHASED_POOL:ADMIN:v1");

            // O saldo Purchased não é consumível antes do ADR 0026: a reserva
            // promocional deve falhar por saldo Promotional zero.
            var error = await Assert.ThrowsAsync<InsufficientBalanceException>(() =>
                wallet.ReservePromotionalAsync(
                    Guid.NewGuid(),
                    Aic.FromAic(1),
                    $"integration:purchased:reserve:{identity.WalletId:N}"));
            Assert.Equal(0, error.AvailableMinor);

            await wallet.GrantPromotionalAsync(
                Aic.FromAic(10),
                $"integration:purchased:promo:{identity.WalletId:N}");
            await wallet.ReservePromotionalAsync(
                Guid.NewGuid(),
                Aic.FromAic(5),
                $"integration:purchased:reserve-ok:{identity.WalletId:N}");
            var withReservation = await wallet.GetSnapshotAsync();
            var open = Assert.Single(withReservation.OpenReservations);
            Assert.Equal(CreditBucket.Promotional, open.Bucket);
            Assert.Equal(Aic.FromAic(5), open.ReservedAmountMinor);
            Assert.True(open.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
        }
        finally
        {
            await CleanupAsync(connectionString, identity);
        }
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
        var transactionIds = db.LedgerTransactions
            .Where(x => x.WalletId == identity.WalletId)
            .Select(x => x.Id);

        await db.LedgerEntries
            .Where(x => transactionIds.Contains(x.TransactionId))
            .ExecuteDeleteAsync();
        await db.BalanceReservations
            .Where(x => x.WalletId == identity.WalletId)
            .ExecuteDeleteAsync();
        await db.LedgerTransactions
            .Where(x => x.WalletId == identity.WalletId)
            .ExecuteDeleteAsync();
        await db.Wallets
            .Where(x => x.Id == identity.WalletId)
            .ExecuteDeleteAsync();
        await db.Accounts
            .Where(x => x.Id == identity.AccountId)
            .ExecuteDeleteAsync();
    }
}
