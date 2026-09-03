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
    public void AdministrativeCreditFillsPurchasedBucketAndIsIdempotentByCorrelation()
    {
        var wallet = NewWallet();
        var correlationId = Guid.NewGuid();

        var (first, firstReplayed) = wallet.GrantPurchased(Aic.FromAic(50), correlationId, Now);
        var (second, secondReplayed) = wallet.GrantPurchased(Aic.FromAic(50), correlationId, Now);

        Assert.False(firstReplayed);
        Assert.True(secondReplayed);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(wallet.Transactions);
        Assert.Equal(Aic.FromAic(50), wallet.GetBalance().AvailableByBucket[CreditBucket.Purchased]);
        Assert.Equal(0, wallet.GetBalance().AvailableByBucket[CreditBucket.Promotional]);
        Assert.All(wallet.Transactions, AssertBalanced);
    }

    [Fact]
    public void AdministrativeCreditRespectsPerRequestCap()
    {
        var wallet = NewWallet();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            wallet.GrantPurchased(WalletLimits.MaximumAdministrativeCreditMinor + 1, Guid.NewGuid(), Now));
        Assert.Empty(wallet.Transactions);
    }

    [Fact]
    public void PurchasedBalanceIsNotConsumableByPromotionalReservation()
    {
        var wallet = NewWallet();
        wallet.GrantPurchased(Aic.FromAic(100), Guid.NewGuid(), Now);

        var error = Assert.Throws<InsufficientBalanceException>(() =>
            wallet.ReservePromotional(Aic.FromAic(1), Guid.NewGuid(), "reserve-purchased", Now, Now.AddMinutes(5)));

        Assert.Equal(0, error.AvailableMinor);
        Assert.Equal(Aic.FromAic(100), wallet.GetBalance().AvailableByBucket[CreditBucket.Purchased]);
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
    public void ExpirationOnlyReleasesReservationAtOrAfterDeadline()
    {
        var wallet = FundedWallet();
        var reservation = wallet.ReservePromotional(
            Aic.FromAic(20), Guid.NewGuid(), "reserve-1", Now, Now.AddMinutes(5));

        Assert.Throws<InvalidOperationException>(() =>
            wallet.Expire(
                reservation.Id,
                "expire-1",
                Now.AddMinutes(5).AddTicks(-1)));
        Assert.Throws<InvalidOperationException>(() =>
            wallet.Capture(
                reservation.Id,
                Aic.FromAic(12),
                "late-capture",
                Now.AddMinutes(5)));

        wallet.Expire(reservation.Id, "expire-1", Now.AddMinutes(5));
        wallet.Expire(reservation.Id, "expire-1", Now.AddMinutes(6));
        wallet.Release(
            reservation.Id,
            "release-after-expiry",
            "Liberação repetida",
            Now.AddMinutes(6));

        Assert.Equal(Aic.FromAic(1_000), wallet.GetBalance().AvailableMinor);
        Assert.Equal(0, wallet.GetBalance().ReservedMinor);
        Assert.Equal(ReservationStatus.Expired, reservation.Status);
        Assert.Single(
            wallet.Transactions,
            x => x.Type == LedgerTransactionType.Release &&
                 x.IdempotencyKey == "expire-1");
        Assert.All(wallet.Transactions, AssertBalanced);
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
