using atos.AIFarm.Core.Wallet;

namespace atos.AIFarm.UnitTests;

public sealed class WalletAggregateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PromotionalGrantCreatesAvailableBalanceAndBalancedEntries()
    {
        var wallet = NewWallet();

        wallet.GrantPromotional(Aic.FromAic(1_000), Guid.NewGuid(), "grant-1", Now);

        Assert.Equal(Aic.FromAic(1_000), wallet.GetBalance().AvailableMinor);
        Assert.All(wallet.Transactions, AssertBalanced);
    }

    [Fact]
    public void ReservationMovesAvailableAmountToReserved()
    {
        var wallet = FundedWallet();

        wallet.ReservePromotional(Aic.FromAic(20), Guid.NewGuid(), "reserve-1", Now, Now.AddMinutes(5));

        Assert.Equal(Aic.FromAic(980), wallet.GetBalance().AvailableMinor);
        Assert.Equal(Aic.FromAic(20), wallet.GetBalance().ReservedMinor);
    }

    [Fact]
    public void InsufficientBalanceDoesNotWriteToLedger()
    {
        var wallet = FundedWallet();
        var transactionCount = wallet.Transactions.Count;

        Assert.Throws<InsufficientBalanceException>(() =>
            wallet.ReservePromotional(Aic.FromAic(1_001), Guid.NewGuid(), "reserve-too-high", Now, Now.AddMinutes(5)));

        Assert.Equal(transactionCount, wallet.Transactions.Count);
        Assert.Equal(Aic.FromAic(1_000), wallet.GetBalance().AvailableMinor);
    }

    [Fact]
    public void CaptureBelowReservationReleasesRemainder()
    {
        var wallet = FundedWallet();
        var reservation = wallet.ReservePromotional(
            Aic.FromAic(20), Guid.NewGuid(), "reserve-1", Now, Now.AddMinutes(5));

        wallet.Capture(reservation.Id, Aic.FromAic(12), "capture-1", Now.AddSeconds(1));

        Assert.Equal(Aic.FromAic(988), wallet.GetBalance().AvailableMinor);
        Assert.Equal(0, wallet.GetBalance().ReservedMinor);
        Assert.Equal(ReservationStatus.Captured, reservation.Status);
        Assert.Contains(wallet.Transactions, x => x.Type == LedgerTransactionType.Release);
        Assert.All(wallet.Transactions, AssertBalanced);
    }

    [Fact]
    public void CaptureEqualToReservationDoesNotCreateRelease()
    {
        var wallet = FundedWallet();
        var reservation = wallet.ReservePromotional(
            Aic.FromAic(20), Guid.NewGuid(), "reserve-1", Now, Now.AddMinutes(5));

        wallet.Capture(reservation.Id, Aic.FromAic(20), "capture-1", Now.AddSeconds(1));

        Assert.Equal(Aic.FromAic(980), wallet.GetBalance().AvailableMinor);
        Assert.Equal(0, wallet.GetBalance().ReservedMinor);
        Assert.DoesNotContain(wallet.Transactions, x => x.Type == LedgerTransactionType.Release);
    }

    [Fact]
    public void ReleaseReturnsTheWholeReservation()
    {
        var wallet = FundedWallet();
        var reservation = wallet.ReservePromotional(
            Aic.FromAic(20), Guid.NewGuid(), "reserve-1", Now, Now.AddMinutes(5));

        wallet.Release(reservation.Id, "release-1", "Falha do provider", Now.AddSeconds(1));

        Assert.Equal(Aic.FromAic(1_000), wallet.GetBalance().AvailableMinor);
        Assert.Equal(0, wallet.GetBalance().ReservedMinor);
        Assert.Equal(ReservationStatus.Released, reservation.Status);
    }

    [Fact]
    public void RepeatedIdempotencyKeysDoNotDuplicateFinancialEffects()
    {
        var wallet = NewWallet();
        var correlationId = Guid.NewGuid();
        wallet.GrantPromotional(Aic.FromAic(1_000), correlationId, "grant-1", Now);
        wallet.GrantPromotional(Aic.FromAic(1_000), correlationId, "grant-1", Now);
        var reservation = wallet.ReservePromotional(
            Aic.FromAic(20), correlationId, "reserve-1", Now, Now.AddMinutes(5));
        wallet.ReservePromotional(Aic.FromAic(20), correlationId, "reserve-1", Now, Now.AddMinutes(5));
        wallet.Capture(reservation.Id, Aic.FromAic(12), "capture-1", Now);
        wallet.Capture(reservation.Id, Aic.FromAic(12), "capture-1", Now);

        Assert.Equal(Aic.FromAic(988), wallet.GetBalance().AvailableMinor);
        Assert.Equal(4, wallet.Transactions.Count);
    }

    private static WalletAggregate NewWallet() => new(Guid.NewGuid(), Guid.NewGuid());

    private static WalletAggregate FundedWallet()
    {
        var wallet = NewWallet();
        wallet.GrantPromotional(Aic.FromAic(1_000), Guid.NewGuid(), "grant-1", Now);
        return wallet;
    }

    private static void AssertBalanced(LedgerTransaction transaction) =>
        Assert.Equal(0, transaction.Entries.Sum(x => x.SignedAmountMinor));
}
