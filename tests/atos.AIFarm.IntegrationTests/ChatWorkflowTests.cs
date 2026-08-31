using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using atos.AIFarm.Infrastructure.Development;

namespace atos.AIFarm.IntegrationTests;

public sealed class ChatWorkflowTests
{
    [Fact]
    public async Task HappyPathReservesTwentyCapturesTwelveAndEndsAtNineHundredEightyEight()
    {
        var wallet = await FundedWalletAsync();
        var workflow = NewWorkflow(wallet, new FakeAiProvider());

        var result = await workflow.SendAsync("Olá, atos", "request-1");
        var snapshot = await wallet.GetSnapshotAsync();

        Assert.Equal(Aic.FromAic(20), result.EstimatedCostMinor);
        Assert.Equal(Aic.FromAic(12), result.ActualCostMinor);
        Assert.Equal(Aic.FromAic(988), snapshot.Balance.AvailableMinor);
        Assert.Equal(0, snapshot.Balance.ReservedMinor);
        Assert.Collection(
            snapshot.Transactions,
            x => Assert.Equal(LedgerTransactionType.Grant, x.Type),
            x => Assert.Equal(LedgerTransactionType.Reserve, x.Type),
            x => Assert.Equal(LedgerTransactionType.Capture, x.Type),
            x => Assert.Equal(LedgerTransactionType.Release, x.Type));
    }

    [Fact]
    public async Task ProviderFailureReleasesReservationWithoutCharge()
    {
        var wallet = await FundedWalletAsync();
        var workflow = NewWorkflow(wallet, new ThrowingProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.SendAsync("falhar", "request-failure"));
        var snapshot = await wallet.GetSnapshotAsync();

        Assert.Equal(Aic.FromAic(1_000), snapshot.Balance.AvailableMinor);
        Assert.Equal(0, snapshot.Balance.ReservedMinor);
        Assert.DoesNotContain(snapshot.Transactions, x => x.Type == LedgerTransactionType.Capture);
    }

    [Fact]
    public async Task RepeatedRequestReturnsPreviousResultWithoutSecondDebit()
    {
        var wallet = await FundedWalletAsync();
        var workflow = NewWorkflow(wallet, new FakeAiProvider());

        await workflow.SendAsync("uma vez", "same-key");
        var replay = await workflow.SendAsync("uma vez", "same-key");
        var snapshot = await wallet.GetSnapshotAsync();

        Assert.True(replay.WasReplayed);
        Assert.Equal(Aic.FromAic(988), snapshot.Balance.AvailableMinor);
        Assert.Single(snapshot.Transactions, x => x.Type == LedgerTransactionType.Capture);
    }

    [Fact]
    public async Task CancellationReleasesReservation()
    {
        var wallet = await FundedWalletAsync();
        var workflow = NewWorkflow(wallet, new WaitingProvider());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workflow.SendAsync("cancelar", "request-cancel", cancellation.Token));
        var snapshot = await wallet.GetSnapshotAsync();

        Assert.Equal(Aic.FromAic(1_000), snapshot.Balance.AvailableMinor);
        Assert.Equal(0, snapshot.Balance.ReservedMinor);
    }

    [Fact]
    public async Task InsufficientBalancePreventsProviderExecution()
    {
        var wallet = new InMemoryWalletService();
        await wallet.GrantPromotionalAsync(Aic.FromAic(10), "small-grant");
        var provider = new CountingProvider();
        var workflow = NewWorkflow(wallet, provider);

        await Assert.ThrowsAsync<InsufficientBalanceException>(() =>
            workflow.SendAsync("sem saldo", "request-no-balance"));

        Assert.Equal(0, provider.CallCount);
    }

    private static ChatWorkflow NewWorkflow(IWalletService wallet, IAiProvider provider) =>
        new(wallet, new FixedPriceEstimator(), new BestAvailableRouteSelector(), provider, new InMemoryChatResultStore());

    private static async Task<InMemoryWalletService> FundedWalletAsync()
    {
        var wallet = new InMemoryWalletService();
        await wallet.GrantPromotionalAsync(Aic.FromAic(1_000), "grant-1");
        return wallet;
    }

    private sealed class ThrowingProvider : IAiProvider
    {
        public Task<AiProviderResult> ExecuteAsync(AiProviderRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Falha simulada");
    }

    private sealed class WaitingProvider : IAiProvider
    {
        public async Task<AiProviderResult> ExecuteAsync(AiProviderRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Inalcançável");
        }
    }

    private sealed class CountingProvider : IAiProvider
    {
        public int CallCount { get; private set; }

        public Task<AiProviderResult> ExecuteAsync(AiProviderRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new AiProviderResult("ok", 1, 1, Aic.FromAic(1)));
        }
    }
}
