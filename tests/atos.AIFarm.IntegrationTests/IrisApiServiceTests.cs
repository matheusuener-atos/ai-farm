using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using atos.AIFarm.Infrastructure.Development;

namespace atos.AIFarm.IntegrationTests;

public sealed class IrisApiServiceTests
{
    [Fact]
    public async Task ExecutionCreatesConversationChargesWalletAndReplaysWithoutSideEffects()
    {
        var tokenId = Guid.NewGuid();
        var wallet = new InMemoryWalletService();
        await wallet.GrantPromotionalAsync(Aic.FromAic(1_000), "api-test-grant");
        var chat = new InMemoryChatStore();
        var budget = new InMemoryAiUsageBudgetService(TimeProvider.System);
        var workflow = new ChatWorkflow(
            wallet,
            new FixedPriceEstimator(),
            new BestAvailableRouteSelector(),
            new FakeAiProvider(),
            chat,
            usageBudget: budget);
        var api = new IrisApiService(chat, workflow);
        var request = new IrisApiExecutionRequest(
            "Explique a operação idempotente",
            MaximumContextMessages: 16,
            DataClassification: AiDataClassification.Standard);

        var first = await api.ExecuteAsync(request, tokenId, "client-request-0001");
        var replay = await api.ExecuteAsync(request, tokenId, "client-request-0001");

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.OperationId, replay.OperationId);
        Assert.Equal(first.ConversationId, replay.ConversationId);
        Assert.Equal(Aic.FromAic(20), first.EstimatedCostMinor);
        Assert.Equal(Aic.FromAic(12), first.ActualCostMinor);
        Assert.Equal(2, (await chat.GetConversationAsync(first.ConversationId))!.Messages.Count);
        Assert.Single((await chat.SearchConversationsAsync(new ConversationQuery())).Items);

        var walletSnapshot = await wallet.GetSnapshotAsync();
        Assert.Equal(Aic.FromAic(988), walletSnapshot.Balance.AvailableMinor);
        Assert.Single(walletSnapshot.Transactions, x => x.Type == LedgerTransactionType.Capture);
        Assert.Equal(Aic.FromAic(12), (await budget.GetSnapshotAsync()).UsedTodayMinor);
    }

    [Fact]
    public async Task IdempotencyIsNamespacedByToken()
    {
        var wallet = new InMemoryWalletService();
        await wallet.GrantPromotionalAsync(Aic.FromAic(1_000), "api-token-namespace-grant");
        var chat = new InMemoryChatStore();
        var workflow = new ChatWorkflow(
            wallet,
            new FixedPriceEstimator(),
            new BestAvailableRouteSelector(),
            new FakeAiProvider(),
            chat);
        var api = new IrisApiService(chat, workflow);
        var request = new IrisApiExecutionRequest("Mesmo identificador em integrações distintas");

        var first = await api.ExecuteAsync(request, Guid.NewGuid(), "shared-request-key");
        var second = await api.ExecuteAsync(request, Guid.NewGuid(), "shared-request-key");

        Assert.False(first.Replayed);
        Assert.False(second.Replayed);
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(2, (await chat.SearchConversationsAsync(new ConversationQuery())).TotalCount);
    }

    [Fact]
    public async Task ExistingConversationMustBelongToCurrentAccountStore()
    {
        var wallet = new InMemoryWalletService();
        await wallet.GrantPromotionalAsync(Aic.FromAic(100), "api-ownership-grant");
        var chat = new InMemoryChatStore();
        var api = new IrisApiService(chat, new ChatWorkflow(
            wallet,
            new FixedPriceEstimator(),
            new BestAvailableRouteSelector(),
            new FakeAiProvider(),
            chat));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            api.ExecuteAsync(
                new IrisApiExecutionRequest("Não executar", Guid.NewGuid()),
                Guid.NewGuid(),
                "unknown-conversation"));

        Assert.Contains("não existe ou não pertence", exception.Message);
        Assert.Single((await wallet.GetSnapshotAsync()).Transactions);
    }
}
