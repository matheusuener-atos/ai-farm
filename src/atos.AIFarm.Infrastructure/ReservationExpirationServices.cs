using System.Data;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure;

public interface IReservationExpirationService
{
    Task<int> ExpireAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);
}

internal sealed class CurrentWalletReservationExpirationService(IWalletService wallet)
    : IReservationExpirationService
{
    public Task<int> ExpireAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        wallet.ExpireReservationsAsync(utcNow, cancellationToken);
}

internal sealed class PostgresReservationExpirationService(
    IDbContextFactory<AIFarmDbContext> factory) : IReservationExpirationService
{
    public async Task<int> ExpireAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        await using var lookup = await factory.CreateDbContextAsync(cancellationToken);
        var candidates = await lookup.BalanceReservations
            .Where(x =>
                x.Status == nameof(ReservationStatus.Active) &&
                x.ExpiresAtUtc <= utcNow)
            .Select(x => new { x.Id, x.WalletId })
            .ToListAsync(cancellationToken);

        var expiredCount = 0;
        foreach (var candidate in candidates)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
            _ = await db.Wallets.FromSqlInterpolated(
                    $"SELECT * FROM aifarm.wallets WHERE \"Id\" = {candidate.WalletId} FOR UPDATE")
                .SingleAsync(cancellationToken);
            var reservation = await db.BalanceReservations.SingleOrDefaultAsync(
                x =>
                    x.Id == candidate.Id &&
                    x.Status == nameof(ReservationStatus.Active) &&
                    x.ExpiresAtUtc <= utcNow,
                cancellationToken);
            if (reservation is null)
            {
                await db.Database.CommitTransactionAsync(cancellationToken);
                continue;
            }

            var bucket = Enum.Parse<CreditBucket>(reservation.Bucket);
            var transactionId = Guid.NewGuid();
            db.LedgerTransactions.Add(new LedgerTransactionRecord
            {
                Id = transactionId,
                WalletId = reservation.WalletId,
                Type = nameof(LedgerTransactionType.Release),
                CorrelationId = reservation.OperationId,
                IdempotencyKey = $"reservation:{reservation.Id:N}:expiry",
                Description = "Liberação automática de reserva expirada",
                CreatedAtUtc = utcNow,
                SettledAtUtc = utcNow,
            });
            db.LedgerEntries.AddRange(
                new LedgerEntryRecord
                {
                    Id = Guid.NewGuid(),
                    TransactionId = transactionId,
                    AccountCode =
                        $"WALLET:{reservation.WalletId}:{bucket}:AVAILABLE",
                    Bucket = bucket.ToString(),
                    Direction = nameof(LedgerDirection.Debit),
                    AmountMinor = reservation.ReservedAmountMinor,
                    CreatedAtUtc = utcNow,
                },
                new LedgerEntryRecord
                {
                    Id = Guid.NewGuid(),
                    TransactionId = transactionId,
                    AccountCode =
                        $"WALLET:{reservation.WalletId}:{bucket}:RESERVED",
                    Bucket = bucket.ToString(),
                    Direction = nameof(LedgerDirection.Credit),
                    AmountMinor = reservation.ReservedAmountMinor,
                    CreatedAtUtc = utcNow,
                });
            reservation.Status = nameof(ReservationStatus.Expired);
            await db.SaveChangesAsync(cancellationToken);
            await db.Database.CommitTransactionAsync(cancellationToken);
            expiredCount++;
        }

        return expiredCount;
    }
}
