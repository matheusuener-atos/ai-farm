using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;

namespace atos.AIFarm.Infrastructure.Development;

public sealed class InMemoryWalletService : IWalletService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WalletAggregate _wallet = new(Guid.NewGuid(), Guid.NewGuid());
    private readonly TimeProvider _timeProvider;

    public InMemoryWalletService()
        : this(TimeProvider.System)
    {
    }

    public InMemoryWalletService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public async Task<WalletSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var openReservations = _wallet.Reservations
                .Where(x => x.Status == ReservationStatus.Active)
                .OrderBy(x => x.ExpiresAtUtc)
                .Select(x => new WalletReservationSnapshot(
                    x.Id,
                    x.Bucket,
                    x.ReservedAmountMinor,
                    x.ExpiresAtUtc))
                .ToArray();
            return new WalletSnapshot(
                _wallet.Id,
                _wallet.GetBalance(),
                [.. _wallet.Transactions],
                openReservations);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PurchasedCreditResult> GrantPurchasedAsync(
        long amountMinor,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException("O CorrelationId é obrigatório.", nameof(correlationId));
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (transaction, wasReplayed) = _wallet.GrantPurchased(
                amountMinor,
                correlationId,
                _timeProvider.GetUtcNow());
            var originalAmount = transaction.Entries.Max(x => x.AmountMinor);
            if (wasReplayed && originalAmount != amountMinor)
            {
                throw new InvalidOperationException(
                    "O CorrelationId já foi usado por um crédito de valor diferente.");
            }
            return new PurchasedCreditResult(
                transaction.Id,
                correlationId,
                originalAmount,
                wasReplayed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task GrantPromotionalAsync(
        long amountMinor,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _wallet.GrantPromotional(amountMinor, Guid.NewGuid(), idempotencyKey, _timeProvider.GetUtcNow());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ReservationResult> ReservePromotionalAsync(
        Guid operationId,
        long amountMinor,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var reservation = _wallet.ReservePromotional(
                amountMinor,
                operationId,
                idempotencyKey,
                now,
                now.AddMinutes(5));
            return new ReservationResult(reservation.Id, reservation.ReservedAmountMinor);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CaptureAsync(
        Guid reservationId,
        long actualAmountMinor,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _wallet.Capture(reservationId, actualAmountMinor, idempotencyKey, _timeProvider.GetUtcNow());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync(
        Guid reservationId,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _wallet.Release(reservationId, idempotencyKey, reason, _timeProvider.GetUtcNow());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ExpireReservationsAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var expired = _wallet.Reservations
                .Where(x => x.Status == ReservationStatus.Active && x.ExpiresAtUtc <= utcNow)
                .ToArray();
            foreach (var reservation in expired)
            {
                _wallet.Expire(
                    reservation.Id,
                    $"reservation:{reservation.Id:N}:expiry",
                    utcNow);
            }

            return expired.Length;
        }
        finally
        {
            _gate.Release();
        }
    }
}
