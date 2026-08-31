using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;

namespace atos.AIFarm.Infrastructure.Development;

public sealed class InMemoryWalletService : IWalletService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WalletAggregate _wallet = new(Guid.NewGuid(), Guid.NewGuid());

    public async Task<WalletSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return new WalletSnapshot(_wallet.Id, _wallet.GetBalance(), [.. _wallet.Transactions]);
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
            _wallet.GrantPromotional(amountMinor, Guid.NewGuid(), idempotencyKey, DateTimeOffset.UtcNow);
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
            var reservation = _wallet.ReservePromotional(
                amountMinor,
                operationId,
                idempotencyKey,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5));
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
            _wallet.Capture(reservationId, actualAmountMinor, idempotencyKey, DateTimeOffset.UtcNow);
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
            _wallet.Release(reservationId, idempotencyKey, reason, DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }
}
