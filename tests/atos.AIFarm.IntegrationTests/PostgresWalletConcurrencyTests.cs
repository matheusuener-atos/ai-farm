using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

public sealed class PostgresWalletConcurrencyTests
{
    private static readonly DateTimeOffset TestNow =
        new(2026, 8, 31, 21, 0, 0, TimeSpan.Zero);

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ConcurrentReservationsCannotOverspendAndStateSurvivesRecreation()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();

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

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task StartupReleasesReservationThatExpiredWhileApplicationWasStopped()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();

        var identity = new AIFarmIdentity(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "atos PostgreSQL expiration test");
        var clock = new ManualTimeProvider(TestNow);
        ReservationResult reservation = null!;

        try
        {
            await using (var firstProvider = BuildProvider(connectionString, identity, clock))
            {
                await firstProvider.InitializeAIFarmAsync();
                var wallet = firstProvider.GetRequiredService<IWalletService>();
                clock.Advance(TimeSpan.FromTicks(1));
                reservation = await wallet.ReservePromotionalAsync(
                    Guid.NewGuid(),
                    Aic.FromAic(800),
                    $"integration:wallet:expiring:{identity.WalletId:N}");

                var beforeRestart = await wallet.GetSnapshotAsync();
                Assert.Equal(Aic.FromAic(200), beforeRestart.Balance.AvailableMinor);
                Assert.Equal(Aic.FromAic(800), beforeRestart.Balance.ReservedMinor);
            }

            clock.Advance(TimeSpan.FromMinutes(6));

            await using var recreatedProvider = BuildProvider(connectionString, identity, clock);
            await recreatedProvider.InitializeAIFarmAsync();
            var recreatedWallet = recreatedProvider.GetRequiredService<IWalletService>();
            var restored = await recreatedWallet.GetSnapshotAsync();

            Assert.Equal(Aic.FromAic(1_000), restored.Balance.AvailableMinor);
            Assert.Equal(0, restored.Balance.ReservedMinor);
            Assert.Collection(
                restored.Transactions.OrderBy(x => x.CreatedAtUtc),
                grant => Assert.Equal(LedgerTransactionType.Grant, grant.Type),
                reserve => Assert.Equal(LedgerTransactionType.Reserve, reserve.Type),
                release => Assert.Equal(LedgerTransactionType.Release, release.Type));

            var factory = recreatedProvider
                .GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var storedReservation = await db.BalanceReservations
                .SingleAsync(x => x.Id == reservation.ReservationId);
            Assert.Equal(nameof(ReservationStatus.Expired), storedReservation.Status);
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
        AIFarmIdentity identity,
        TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }
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


    private sealed record ReservationAttempt(
        ReservationResult? Reservation,
        Exception? Error);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
