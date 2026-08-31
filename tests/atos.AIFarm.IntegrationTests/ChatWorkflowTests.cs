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
    public async Task PreparingShowsRouteAndLimitWithoutCallingProviderOrChangingLedger()
    {
        var wallet = await FundedWalletAsync();
        var provider = new CountingProvider();
        var chat = new InMemoryChatStore();
        var workflow = NewWorkflow(wallet, provider, chat);
        var conversation = await chat.GetOrCreateCurrentConversationAsync();

        var plan = await workflow.PrepareAsync("  confirmar antes  ");
        var afterPreparation = await wallet.GetSnapshotAsync();

        Assert.Equal("confirmar antes", plan.Prompt);
        Assert.Equal("atos Fake Provider", plan.Route.Provider);
        Assert.Equal("atos-fake-chat-v1", plan.Route.Model);
        Assert.Equal(Aic.FromAic(20), plan.MaximumCostMinor);
        Assert.Equal(0, provider.CallCount);
        Assert.Single(afterPreparation.Transactions);
        Assert.Equal(Aic.FromAic(1_000), afterPreparation.Balance.AvailableMinor);

        var result = await workflow.SendAsync(
            conversation.Id,
            plan,
            "confirmed-request");
        var afterConfirmation = await wallet.GetSnapshotAsync();

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(plan.Route, result.Route);
        Assert.Equal(plan.MaximumCostMinor, result.EstimatedCostMinor);
        Assert.Equal(Aic.FromAic(999), afterConfirmation.Balance.AvailableMinor);
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

    [Fact]
    public async Task ConversationHistorySurvivesWorkflowRecreation()
    {
        var wallet = await FundedWalletAsync();
        var chat = new InMemoryChatStore();
        var conversation = await chat.GetOrCreateCurrentConversationAsync();
        var firstWorkflow = NewWorkflow(wallet, new FakeAiProvider(), chat);

        var result = await firstWorkflow.SendAsync(
            conversation.Id,
            "persista esta conversa",
            "persistent-request");

        var recreatedWorkflow = NewWorkflow(wallet, new FakeAiProvider(), chat);
        var restored = await chat.GetOrCreateCurrentConversationAsync();

        Assert.Equal(result.ConversationId, restored.Id);
        Assert.Equal(2, restored.Messages.Count);
        Assert.Collection(
            restored.Messages,
            message =>
            {
                Assert.Equal(ChatMessageRole.User, message.Role);
                Assert.Equal(result.OperationId, message.OperationId);
                Assert.Equal("persista esta conversa", message.Content);
            },
            message =>
            {
                Assert.Equal(ChatMessageRole.Assistant, message.Role);
                Assert.Equal(result.OperationId, message.OperationId);
                Assert.NotNull(message.Route);
                Assert.Equal(result.ActualCostMinor, message.ActualCostMinor);
            });
        _ = recreatedWorkflow;
    }

    private static ChatWorkflow NewWorkflow(
        IWalletService wallet,
        IAiProvider provider,
        IChatStore? chat = null) =>
        new(
            wallet,
            new FixedPriceEstimator(),
            new BestAvailableRouteSelector(),
            provider,
            chat ?? new InMemoryChatStore());

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
