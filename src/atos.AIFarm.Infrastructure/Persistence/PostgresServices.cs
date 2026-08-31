using System.Data;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

internal static class DevelopmentIdentity
{
    public static readonly Guid AccountId = Guid.Parse("a7a0a7a0-0000-4000-8000-000000000001");
    public static readonly Guid WalletId = Guid.Parse("a7a0a7a0-0000-4000-8000-000000000002");
}

public sealed class PostgresWalletService(IDbContextFactory<AIFarmDbContext> factory) : IWalletService
{
    public async Task<WalletSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var transactionRecords = await db.LedgerTransactions
            .Where(x => x.WalletId == DevelopmentIdentity.WalletId)
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var transactionIds = transactionRecords.Select(x => x.Id).ToArray();
        var entryRecords = await db.LedgerEntries
            .Where(x => transactionIds.Contains(x.TransactionId))
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var entriesByTransaction = entryRecords
            .GroupBy(x => x.TransactionId)
            .ToDictionary(x => x.Key, x => x.Select(ToDomain).ToArray());
        var transactions = transactionRecords.Select(record => new LedgerTransaction(
            record.Id,
            record.WalletId,
            Enum.Parse<LedgerTransactionType>(record.Type),
            record.CorrelationId,
            record.IdempotencyKey,
            record.Description,
            record.CreatedAtUtc,
            entriesByTransaction.GetValueOrDefault(record.Id, []))).ToArray();

        var availableByBucket = Enum.GetValues<CreditBucket>().ToDictionary(
            bucket => bucket,
            bucket => BalanceFor(entryRecords, AvailableAccount(bucket)));
        var reserved = Enum.GetValues<CreditBucket>()
            .Sum(bucket => BalanceFor(entryRecords, ReservedAccount(bucket)));
        return new WalletSnapshot(
            DevelopmentIdentity.WalletId,
            new WalletBalance(availableByBucket.Values.Sum(), reserved, availableByBucket),
            transactions);
    }

    public async Task GrantPromotionalAsync(
        long amountMinor,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        EnsurePositive(amountMinor);
        await using var db = await OpenLockedAsync(cancellationToken);
        if (await db.LedgerTransactions.AnyAsync(
                x => x.WalletId == DevelopmentIdentity.WalletId &&
                     x.IdempotencyKey == idempotencyKey,
                cancellationToken))
        {
            await db.Database.CommitTransactionAsync(cancellationToken);
            return;
        }

        AddBalancedTransaction(
            db,
            LedgerTransactionType.Grant,
            Guid.NewGuid(),
            idempotencyKey,
            "Crédito promocional de desenvolvimento",
            DateTimeOffset.UtcNow,
            (AvailableAccount(CreditBucket.Promotional), CreditBucket.Promotional, LedgerDirection.Debit, amountMinor),
            ("atos:PROMOTIONAL_POOL", CreditBucket.Promotional, LedgerDirection.Credit, amountMinor));
        await SaveAndCommitAsync(db, cancellationToken);
    }

    public async Task<ReservationResult> ReservePromotionalAsync(
        Guid operationId,
        long amountMinor,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        EnsurePositive(amountMinor);
        await using var db = await OpenLockedAsync(cancellationToken);
        var existing = await db.BalanceReservations.SingleOrDefaultAsync(
            x => x.WalletId == DevelopmentIdentity.WalletId &&
                 x.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            await db.Database.CommitTransactionAsync(cancellationToken);
            return new ReservationResult(existing.Id, existing.ReservedAmountMinor);
        }

        var availableCode = AvailableAccount(CreditBucket.Promotional);
        var available = await (
            from entry in db.LedgerEntries
            join transaction in db.LedgerTransactions on entry.TransactionId equals transaction.Id
            where transaction.WalletId == DevelopmentIdentity.WalletId &&
                  entry.AccountCode == availableCode
            select entry.Direction == nameof(LedgerDirection.Debit)
                ? entry.AmountMinor
                : -entry.AmountMinor).SumAsync(cancellationToken);
        if (available < amountMinor)
        {
            throw new InsufficientBalanceException(available, amountMinor);
        }

        var now = DateTimeOffset.UtcNow;
        var reservation = new BalanceReservationRecord
        {
            Id = Guid.NewGuid(),
            WalletId = DevelopmentIdentity.WalletId,
            OperationId = operationId,
            Bucket = nameof(CreditBucket.Promotional),
            ReservedAmountMinor = amountMinor,
            Status = nameof(ReservationStatus.Active),
            ExpiresAtUtc = now.AddMinutes(5),
            IdempotencyKey = idempotencyKey
        };
        db.BalanceReservations.Add(reservation);
        AddBalancedTransaction(
            db,
            LedgerTransactionType.Reserve,
            operationId,
            $"{idempotencyKey}:ledger",
            "Reserva para execução de IA",
            now,
            (ReservedAccount(CreditBucket.Promotional), CreditBucket.Promotional, LedgerDirection.Debit, amountMinor),
            (availableCode, CreditBucket.Promotional, LedgerDirection.Credit, amountMinor));
        await SaveAndCommitAsync(db, cancellationToken);
        return new ReservationResult(reservation.Id, amountMinor);
    }

    public async Task CaptureAsync(
        Guid reservationId,
        long actualAmountMinor,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await using var db = await OpenLockedAsync(cancellationToken);
        if (await TransactionExistsAsync(db, idempotencyKey, cancellationToken))
        {
            await db.Database.CommitTransactionAsync(cancellationToken);
            return;
        }

        var reservation = await db.BalanceReservations.SingleAsync(
            x => x.Id == reservationId && x.WalletId == DevelopmentIdentity.WalletId,
            cancellationToken);
        if (reservation.Status != nameof(ReservationStatus.Active) ||
            actualAmountMinor <= 0 ||
            actualAmountMinor > reservation.ReservedAmountMinor)
        {
            throw new InvalidOperationException("A reserva não pode ser capturada.");
        }

        var bucket = Enum.Parse<CreditBucket>(reservation.Bucket);
        var now = DateTimeOffset.UtcNow;
        AddBalancedTransaction(
            db,
            LedgerTransactionType.Capture,
            reservation.OperationId,
            idempotencyKey,
            "Liquidação do custo real de IA",
            now,
            ("atos:AI_CONSUMPTION", bucket, LedgerDirection.Debit, actualAmountMinor),
            (ReservedAccount(bucket), bucket, LedgerDirection.Credit, actualAmountMinor));

        var remainder = reservation.ReservedAmountMinor - actualAmountMinor;
        if (remainder > 0)
        {
            AddBalancedTransaction(
                db,
                LedgerTransactionType.Release,
                reservation.OperationId,
                $"{idempotencyKey}:remainder",
                "Liberação da diferença entre estimativa e custo real",
                now,
                (AvailableAccount(bucket), bucket, LedgerDirection.Debit, remainder),
                (ReservedAccount(bucket), bucket, LedgerDirection.Credit, remainder));
        }

        reservation.CapturedAmountMinor = actualAmountMinor;
        reservation.Status = nameof(ReservationStatus.Captured);
        await SaveAndCommitAsync(db, cancellationToken);
    }

    public async Task ReleaseAsync(
        Guid reservationId,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await using var db = await OpenLockedAsync(cancellationToken);
        if (await TransactionExistsAsync(db, idempotencyKey, cancellationToken))
        {
            await db.Database.CommitTransactionAsync(cancellationToken);
            return;
        }

        var reservation = await db.BalanceReservations.SingleAsync(
            x => x.Id == reservationId && x.WalletId == DevelopmentIdentity.WalletId,
            cancellationToken);
        if (reservation.Status == nameof(ReservationStatus.Released))
        {
            await db.Database.CommitTransactionAsync(cancellationToken);
            return;
        }

        if (reservation.Status != nameof(ReservationStatus.Active))
        {
            throw new InvalidOperationException("Somente uma reserva ativa pode ser liberada.");
        }

        var bucket = Enum.Parse<CreditBucket>(reservation.Bucket);
        AddBalancedTransaction(
            db,
            LedgerTransactionType.Release,
            reservation.OperationId,
            idempotencyKey,
            reason,
            DateTimeOffset.UtcNow,
            (AvailableAccount(bucket), bucket, LedgerDirection.Debit, reservation.ReservedAmountMinor),
            (ReservedAccount(bucket), bucket, LedgerDirection.Credit, reservation.ReservedAmountMinor));
        reservation.Status = nameof(ReservationStatus.Released);
        await SaveAndCommitAsync(db, cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.Accounts.AnyAsync(x => x.Id == DevelopmentIdentity.AccountId, cancellationToken))
        {
            db.Accounts.Add(new AccountRecord
            {
                Id = DevelopmentIdentity.AccountId,
                DisplayName = "atos Development",
                Status = "Active",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            db.Wallets.Add(new WalletRecord
            {
                Id = DevelopmentIdentity.WalletId,
                AccountId = DevelopmentIdentity.AccountId,
                Unit = "AIC",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<AIFarmDbContext> OpenLockedAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        _ = await db.Wallets.FromSqlInterpolated(
                $"SELECT * FROM aifarm.wallets WHERE \"Id\" = {DevelopmentIdentity.WalletId} FOR UPDATE")
            .SingleAsync(cancellationToken);
        return db;
    }

    private static async Task SaveAndCommitAsync(
        AIFarmDbContext db,
        CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        await db.Database.CommitTransactionAsync(cancellationToken);
    }

    private static Task<bool> TransactionExistsAsync(
        AIFarmDbContext db,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        db.LedgerTransactions.AnyAsync(
            x => x.WalletId == DevelopmentIdentity.WalletId &&
                 x.IdempotencyKey == idempotencyKey,
            cancellationToken);

    private static void AddBalancedTransaction(
        AIFarmDbContext db,
        LedgerTransactionType type,
        Guid correlationId,
        string idempotencyKey,
        string description,
        DateTimeOffset now,
        params (string Account, CreditBucket Bucket, LedgerDirection Direction, long Amount)[] entries)
    {
        if (entries.Sum(x => x.Direction == LedgerDirection.Debit ? x.Amount : -x.Amount) != 0)
        {
            throw new InvalidOperationException("Todo lançamento deve ser balanceado.");
        }

        var transactionId = Guid.NewGuid();
        db.LedgerTransactions.Add(new LedgerTransactionRecord
        {
            Id = transactionId,
            WalletId = DevelopmentIdentity.WalletId,
            Type = type.ToString(),
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            Description = description,
            CreatedAtUtc = now,
            SettledAtUtc = now
        });
        db.LedgerEntries.AddRange(entries.Select(entry => new LedgerEntryRecord
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            AccountCode = entry.Account,
            Bucket = entry.Bucket.ToString(),
            Direction = entry.Direction.ToString(),
            AmountMinor = entry.Amount,
            CreatedAtUtc = now
        }));
    }

    private static LedgerEntry ToDomain(LedgerEntryRecord entry) => new(
        entry.Id,
        entry.AccountCode,
        Enum.Parse<CreditBucket>(entry.Bucket),
        Enum.Parse<LedgerDirection>(entry.Direction),
        entry.AmountMinor,
        entry.CreatedAtUtc);

    private static long BalanceFor(IEnumerable<LedgerEntryRecord> entries, string account) =>
        entries.Where(x => x.AccountCode == account)
            .Sum(x => x.Direction == nameof(LedgerDirection.Debit)
                ? x.AmountMinor
                : -x.AmountMinor);

    private static string AvailableAccount(CreditBucket bucket) =>
        $"WALLET:{DevelopmentIdentity.WalletId}:{bucket}:AVAILABLE";

    private static string ReservedAccount(CreditBucket bucket) =>
        $"WALLET:{DevelopmentIdentity.WalletId}:{bucket}:RESERVED";

    private static void EnsurePositive(long amountMinor)
    {
        if (amountMinor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountMinor));
        }
    }
}

public sealed class PostgresChatResultStore(IDbContextFactory<AIFarmDbContext> factory)
    : IChatResultStore
{
    public async Task<ChatExecutionResult?> FindAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var stored = await (
            from request in db.AiRequests
            join execution in db.AiExecutions on request.Id equals execution.AiRequestId
            where request.AccountId == DevelopmentIdentity.AccountId &&
                  request.IdempotencyKey == idempotencyKey
            select new { request, execution }).SingleOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return null;
        }

        return new ChatExecutionResult(
            stored.request.ConversationId,
            stored.execution.Response,
            new AiRoute(
                stored.execution.Provider,
                stored.execution.Model,
                stored.execution.PriceCatalogVersion),
            stored.execution.EstimatedCostMinor,
            stored.execution.ActualCostMinor,
            true);
    }

    public async Task SaveAsync(
        string idempotencyKey,
        ChatExecutionResult result,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.AiRequests.AnyAsync(
                x => x.AccountId == DevelopmentIdentity.AccountId &&
                     x.IdempotencyKey == idempotencyKey,
                cancellationToken))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid();
        db.AiRequests.Add(new AiRequestRecord
        {
            Id = requestId,
            AccountId = DevelopmentIdentity.AccountId,
            ConversationId = result.OperationId,
            Modality = "Chat",
            DataClassification = "Standard",
            RequestedRoute = $"{result.Route.Provider}/{result.Route.Model}",
            MaxCostMinor = result.EstimatedCostMinor,
            Status = "Completed",
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = now
        });
        db.AiExecutions.Add(new AiExecutionRecord
        {
            Id = Guid.NewGuid(),
            AiRequestId = requestId,
            Provider = result.Route.Provider,
            Model = result.Route.Model,
            PriceCatalogVersion = result.Route.PriceCatalogVersion,
            EstimatedCostMinor = result.EstimatedCostMinor,
            ActualCostMinor = result.ActualCostMinor,
            Status = "Completed",
            Response = result.Response,
            StartedAtUtc = now,
            FinishedAtUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
