using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

public sealed class PostgresWalletConcurrencyTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ConcurrentReservationsCannotOverspendAndStateSurvivesRecreation()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var identity = new AIFarmIdentity(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "atos PostgreSQL integration test");
        var grantAmount = Aic.FromAic(100);
        var reservationAmount = Aic.FromAic(80);

        try
        {
            await using var firstProvider = BuildProvider(connectionString, identity);
            await using var secondProvider = BuildProvider(connectionString, identity);
            var firstWallet = firstProvider.GetRequiredService<IWalletService>();
            var secondWallet = secondProvider.GetRequiredService<IWalletService>();

            await firstWallet.GrantPromotionalAsync(
                grantAmount,
                $"integration:wallet:grant:{identity.WalletId:N}");

            var attempts = await Task.WhenAll(
                TryReserveAsync(firstWallet, identity.WalletId, reservationAmount, "first"),
                TryReserveAsync(secondWallet, identity.WalletId, reservationAmount, "second"));

            var successful = Assert.Single(attempts, x => x.Reservation is not null);
            var rejected = Assert.Single(attempts, x => x.Error is not null);
            var insufficient = Assert.IsType<InsufficientBalanceException>(rejected.Error);
            Assert.Equal(Aic.FromAic(20), insufficient.AvailableMinor);
            Assert.Equal(reservationAmount, insufficient.RequestedMinor);

            await using var recreatedProvider = BuildProvider(connectionString, identity);
            var recreatedWallet = recreatedProvider.GetRequiredService<IWalletService>();
            var snapshot = await recreatedWallet.GetSnapshotAsync();

            Assert.Equal(Aic.FromAic(20), snapshot.Balance.AvailableMinor);
            Assert.Equal(reservationAmount, snapshot.Balance.ReservedMinor);
            Assert.Collection(
                snapshot.Transactions.OrderBy(x => x.CreatedAtUtc),
                grant => Assert.Equal(LedgerTransactionType.Grant, grant.Type),
                reserve => Assert.Equal(LedgerTransactionType.Reserve, reserve.Type));
            Assert.All(
                snapshot.Transactions,
                transaction => Assert.Equal(0, transaction.Entries.Sum(x => x.SignedAmountMinor)));
            Assert.NotNull(successful.Reservation);
        }
        finally
        {
            await CleanupAsync(connectionString, identity);
        }
    }

    private static async Task<ReservationAttempt> TryReserveAsync(
        IWalletService wallet,
        Guid walletId,
        long amountMinor,
        string suffix)
    {
        try
        {
            var result = await wallet.ReservePromotionalAsync(
                Guid.NewGuid(),
                amountMinor,
                $"integration:wallet:reserve:{walletId:N}:{suffix}");
            return new ReservationAttempt(result, null);
        }
        catch (Exception exception)
        {
            return new ReservationAttempt(null, exception);
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

    private static string? ReadLocalVariable(string key)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, ".env.local");
            if (File.Exists(path))
            {
                var prefix = $"{key}=";
                var line = File.ReadLines(path)
                    .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
                return line?[prefix.Length..].Trim().Trim('"', '\'');
            }
            directory = directory.Parent;
        }
        return null;
    }

    private sealed record ReservationAttempt(
        ReservationResult? Reservation,
        Exception? Error);
}
