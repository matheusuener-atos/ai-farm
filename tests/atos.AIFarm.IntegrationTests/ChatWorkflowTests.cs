using System.Text;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using atos.AIFarm.Infrastructure.Development;
using atos.AIFarm.Infrastructure.Attachments;

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
        Assert.Equal("Olá, atos".Length, result.InputUnits);
        Assert.True(result.OutputUnits > 0);
        Assert.NotNull(result.StartedAtUtc);
        Assert.NotNull(result.FinishedAtUtc);
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
        var chat = new InMemoryChatStore();
        var workflow = NewWorkflow(wallet, new ThrowingProvider(), chat);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.SendAsync("falhar", "request-failure"));
        var snapshot = await wallet.GetSnapshotAsync();

        Assert.Equal(Aic.FromAic(1_000), snapshot.Balance.AvailableMinor);
        Assert.Equal(0, snapshot.Balance.ReservedMinor);
        Assert.DoesNotContain(snapshot.Transactions, x => x.Type == LedgerTransactionType.Capture);
        var failed = Assert.Single((await chat.GetDashboardAsync()).RecentExecutions);
        Assert.Equal("ProviderFailure", failed.Status);
        Assert.Equal(0, failed.ActualCostMinor);
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
    public async Task RepeatedStreamingAttemptReplaysWithoutCallingProviderOrChargingAgain()
    {
        var wallet = await FundedWalletAsync();
        var chat = new InMemoryChatStore();
        var conversation = await chat.CreateConversationAsync();
        var provider = new CountingStreamingProvider();
        var workflow = NewWorkflow(wallet, provider, chat);
        var plan = await workflow.PrepareAsync("não duplique");
        var firstContent = new StringBuilder();
        var replayContent = new StringBuilder();

        var first = await workflow.SendStreamingAsync(
            conversation.Id,
            plan,
            "same-stream-attempt",
            (delta, _) =>
            {
                firstContent.Append(delta);
                return ValueTask.CompletedTask;
            });
        var replay = await workflow.SendStreamingAsync(
            conversation.Id,
            plan,
            "same-stream-attempt",
            (delta, _) =>
            {
                replayContent.Append(delta);
                return ValueTask.CompletedTask;
            });
        var snapshot = await wallet.GetSnapshotAsync();

        Assert.False(first.WasReplayed);
        Assert.True(replay.WasReplayed);
        Assert.Equal(first.Response, firstContent.ToString());
        Assert.Equal(first.Response, replayContent.ToString());
        Assert.Equal(1, provider.CallCount);
        Assert.Single(snapshot.Transactions, x => x.Type == LedgerTransactionType.Capture);
    }

    [Fact]
    public async Task RetryingFailedExecutionWithNewAttemptChargesOnlySuccessfulAttempt()
    {
        var wallet = await FundedWalletAsync();
        var chat = new InMemoryChatStore();
        var conversation = await chat.CreateConversationAsync();
        var provider = new FailOnceProvider();
        var workflow = NewWorkflow(wallet, provider, chat);
        var plan = await workflow.PrepareAsync("tente de novo");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.SendAsync(conversation.Id, plan, "attempt-1"));
        var result = await workflow.SendAsync(
            conversation.Id,
            plan,
            "attempt-2");
        var snapshot = await wallet.GetSnapshotAsync();
        var dashboard = await chat.GetDashboardAsync();

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(Aic.FromAic(12), result.ActualCostMinor);
        Assert.Equal(Aic.FromAic(988), snapshot.Balance.AvailableMinor);
        Assert.Single(snapshot.Transactions, x => x.Type == LedgerTransactionType.Capture);
        Assert.Equal(2, dashboard.Totals.ExecutionCount);
        Assert.Contains(dashboard.RecentExecutions, x => x.Status == "ProviderFailure");
        Assert.Contains(dashboard.RecentExecutions, x => x.Status == "Completed");
    }

    [Fact]
    public async Task CancellationReleasesReservation()
    {
        var wallet = await FundedWalletAsync();
        var chat = new InMemoryChatStore();
        var provider = new WaitingProvider();
        var workflow = NewWorkflow(wallet, provider, chat);
        using var cancellation = new CancellationTokenSource();

        var execution = workflow.SendAsync(
            "cancelar",
            "request-cancel",
            cancellation.Token);
        await provider.Started;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        var snapshot = await wallet.GetSnapshotAsync();

        Assert.Equal(Aic.FromAic(1_000), snapshot.Balance.AvailableMinor);
        Assert.Equal(0, snapshot.Balance.ReservedMinor);
        Assert.Equal(
            "Cancelled",
            Assert.Single((await chat.GetDashboardAsync()).RecentExecutions).Status);
    }

    [Fact]
    public async Task StreamingPublishesDeltasThenPersistsCompletedResponse()
    {
        var wallet = await FundedWalletAsync();
        var chat = new InMemoryChatStore();
        var conversation = await chat.CreateConversationAsync();
        var workflow = NewWorkflow(wallet, new FakeAiProvider(), chat);
        var plan = await workflow.PrepareAsync("responda em partes");
        var content = new StringBuilder();

        var result = await workflow.SendStreamingAsync(
            conversation.Id,
            plan,
            "stream-success",
            (delta, _) =>
            {
                content.Append(delta);
                return ValueTask.CompletedTask;
            });
        var stored = await chat.GetConversationAsync(conversation.Id);

        Assert.Equal(result.Response, content.ToString());
        Assert.Equal(result.Response, stored!.Messages.Last().Content);
        Assert.Equal(ChatMessageStatus.Completed, stored.Messages.Last().Status);
        Assert.Equal(Aic.FromAic(988), (await wallet.GetSnapshotAsync()).Balance.AvailableMinor);
    }

    [Fact]
    public async Task CancellingMidStreamDiscardsPartialResponseAndReleasesReservation()
    {
        var wallet = await FundedWalletAsync();
        var chat = new InMemoryChatStore();
        var conversation = await chat.CreateConversationAsync();
        var provider = new StreamingWaitingProvider();
        var workflow = NewWorkflow(wallet, provider, chat);
        var plan = await workflow.PrepareAsync("interromper stream");
        using var cancellation = new CancellationTokenSource();
        var content = new StringBuilder();

        var execution = workflow.SendStreamingAsync(
            conversation.Id,
            plan,
            "stream-cancel",
            (delta, _) =>
            {
                content.Append(delta);
                return ValueTask.CompletedTask;
            },
            cancellation.Token);
        await provider.Started;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        var snapshot = await wallet.GetSnapshotAsync();
        var stored = await chat.GetConversationAsync(conversation.Id);

        Assert.Equal("parcial", content.ToString());
        Assert.Empty(stored!.Messages);
        Assert.Equal(Aic.FromAic(1_000), snapshot.Balance.AvailableMinor);
        Assert.Equal(0, snapshot.Balance.ReservedMinor);
        Assert.Equal(
            "Cancelled",
            Assert.Single((await chat.GetDashboardAsync()).RecentExecutions).Status);
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

    [Fact]
    public async Task ConversationsCanBeCreatedListedAndLoadedIndependently()
    {
        var chat = new InMemoryChatStore();
        var first = await chat.CreateConversationAsync();
        var second = await chat.CreateConversationAsync();

        await chat.SaveCompletedAsync(
            "histórico da primeira",
            "first-conversation",
            new ChatExecutionResult(
                first.Id,
                Guid.NewGuid(),
                "resposta da primeira",
                new AiRoute("test", "test", "v1"),
                Aic.FromAic(2),
                Aic.FromAic(1),
                false),
            CancellationToken.None);

        var summaries = await chat.ListConversationsAsync();
        var restoredFirst = await chat.GetConversationAsync(first.Id);
        var restoredSecond = await chat.GetConversationAsync(second.Id);

        Assert.Equal(2, summaries.Count);
        Assert.Equal(first.Id, summaries[0].Id);
        Assert.Equal("histórico da primeira", summaries[0].Title);
        Assert.Equal(2, summaries[0].MessageCount);
        Assert.Equal("resposta da primeira", summaries[0].Preview);
        Assert.Equal(2, restoredFirst?.Messages.Count);
        Assert.Empty(restoredSecond!.Messages);

        Assert.True(await chat.RenameConversationAsync(
            first.Id,
            "  Projeto   principal  "));
        Assert.False(await chat.RenameConversationAsync(
            Guid.NewGuid(),
            "não existe"));
        Assert.Equal(
            "Projeto principal",
            (await chat.ListConversationsAsync())
                .Single(x => x.Id == first.Id)
                .Title);

        Assert.True(await chat.ArchiveConversationAsync(first.Id));
        Assert.False(await chat.ArchiveConversationAsync(first.Id));
        Assert.Null(await chat.GetConversationAsync(first.Id));
        Assert.Equal(second.Id, Assert.Single(await chat.ListConversationsAsync()).Id);
        var archivedSummary = Assert.Single(
            await chat.ListArchivedConversationsAsync());
        var archivedPage = await chat.SearchConversationsAsync(
            new ConversationQuery("projeto", 1, 8, true));
        Assert.Equal(first.Id, archivedSummary.Id);
        Assert.Equal("Projeto principal", archivedSummary.Title);
        Assert.Equal(2, archivedSummary.MessageCount);
        Assert.Equal(first.Id, Assert.Single(archivedPage.Items).Id);

        Assert.True(await chat.RestoreConversationAsync(first.Id));
        Assert.False(await chat.RestoreConversationAsync(first.Id));
        Assert.Empty(await chat.ListArchivedConversationsAsync());
        Assert.Equal(2, (await chat.GetConversationAsync(first.Id))?.Messages.Count);
        var restoredSummaries = await chat.ListConversationsAsync();
        Assert.Equal(2, restoredSummaries.Count);
        Assert.Equal(
            "Projeto principal",
            restoredSummaries.Single(x => x.Id == first.Id).Title);

        var titleSearch = await chat.SearchConversationsAsync(
            new ConversationQuery("projeto", 1, 8));
        var contentSearch = await chat.SearchConversationsAsync(
            new ConversationQuery("resposta da primeira", 1, 8));
        var firstPage = await chat.SearchConversationsAsync(
            new ConversationQuery(Page: 1, PageSize: 1));
        var secondPage = await chat.SearchConversationsAsync(
            new ConversationQuery(Page: 2, PageSize: 1));

        Assert.Equal(first.Id, Assert.Single(titleSearch.Items).Id);
        Assert.Equal(first.Id, Assert.Single(contentSearch.Items).Id);
        Assert.Equal(2, firstPage.TotalCount);
        Assert.Equal(2, firstPage.TotalPages);
        Assert.True(firstPage.HasNextPage);
        Assert.Single(firstPage.Items);
        Assert.True(secondPage.HasPreviousPage);
        Assert.Single(secondPage.Items);
        Assert.NotEqual(firstPage.Items[0].Id, secondPage.Items[0].Id);

        Assert.True(await chat.DeleteConversationAsync(first.Id));
        Assert.False(await chat.DeleteConversationAsync(first.Id));
        Assert.Null(await chat.GetConversationAsync(first.Id));
        Assert.Equal(second.Id, Assert.Single(
            (await chat.SearchConversationsAsync(new ConversationQuery())).Items).Id);
        Assert.Empty((await chat.SearchConversationsAsync(
            new ConversationQuery("resposta da primeira"))).Items);
        Assert.Single((await chat.GetDashboardAsync()).RecentExecutions);
    }

    [Fact]
    public async Task EditingFromMessageCreatesBranchAndPreservesOriginalHistory()
    {
        var chat = new InMemoryChatStore();
        var original = await chat.CreateConversationAsync();
        var firstOperation = Guid.NewGuid();
        var secondOperation = Guid.NewGuid();
        await chat.SaveCompletedAsync(
            "primeira pergunta",
            "branch:first",
            new ChatExecutionResult(
                original.Id,
                firstOperation,
                "primeira resposta",
                new AiRoute("test", "test", "v1"),
                Aic.FromAic(2),
                Aic.FromAic(1),
                false),
            CancellationToken.None);
        await chat.SaveCompletedAsync(
            "pergunta que será editada",
            "branch:second",
            new ChatExecutionResult(
                original.Id,
                secondOperation,
                "resposta original preservada",
                new AiRoute("test", "test", "v1"),
                Aic.FromAic(2),
                Aic.FromAic(1),
                false),
            CancellationToken.None);

        var beforeBranch = await chat.GetConversationAsync(original.Id);
        var branchPoint = beforeBranch!.Messages.Single(
            x => x.OperationId == secondOperation && x.Role == ChatMessageRole.User);
        var branch = await chat.BranchConversationAsync(
            original.Id,
            branchPoint.Id);

        Assert.Equal(original.Id, branch.ParentConversationId);
        Assert.Equal(branchPoint.Id, branch.BranchedFromMessageId);
        Assert.EndsWith(" / RAMO", branch.Title);
        Assert.Collection(
            branch.Messages,
            message => Assert.Equal("primeira pergunta", message.Content),
            message => Assert.Equal("primeira resposta", message.Content));
        Assert.DoesNotContain(branch.Messages, x => x.Id == beforeBranch.Messages[0].Id);

        await chat.SaveCompletedAsync(
            "pergunta editada",
            "branch:edited",
            new ChatExecutionResult(
                branch.Id,
                Guid.NewGuid(),
                "nova continuação",
                new AiRoute("test", "test", "v1"),
                Aic.FromAic(2),
                Aic.FromAic(1),
                false),
            CancellationToken.None);

        var unchangedOriginal = await chat.GetConversationAsync(original.Id);
        var completedBranch = await chat.GetConversationAsync(branch.Id);
        var branchSummary = (await chat.ListConversationsAsync())
            .Single(x => x.Id == branch.Id);

        Assert.Equal(4, unchangedOriginal!.Messages.Count);
        Assert.Contains(
            unchangedOriginal.Messages,
            x => x.Content == "resposta original preservada");
        Assert.Equal(4, completedBranch!.Messages.Count);
        Assert.Equal("pergunta editada", completedBranch.Messages[^2].Content);
        Assert.Equal("nova continuação", completedBranch.Messages[^1].Content);
        Assert.Equal(original.Id, branchSummary.ParentConversationId);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(1, 0)]
    [InlineData(1, 51)]
    public void ConversationQueryRejectsInvalidPagination(int page, int pageSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConversationQuery(Page: page, PageSize: pageSize).Normalize());
    }

    [Fact]
    public async Task UnknownConversationIsRejectedBeforeWalletReservation()
    {
        var wallet = await FundedWalletAsync();
        var provider = new CountingProvider();
        var workflow = NewWorkflow(wallet, provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.SendAsync(Guid.NewGuid(), "não cobrar", "unknown-conversation"));
        var snapshot = await wallet.GetSnapshotAsync();

        Assert.Equal(0, provider.CallCount);
        Assert.Single(snapshot.Transactions);
        Assert.Equal(Aic.FromAic(1_000), snapshot.Balance.AvailableMinor);
    }

    [Fact]
    public async Task UsageDashboardAggregatesMeasuredUnitsCostsAndExecutions()
    {
        var wallet = await FundedWalletAsync();
        var chat = new InMemoryChatStore();
        var conversation = await chat.CreateConversationAsync();
        var workflow = NewWorkflow(wallet, new FakeAiProvider(), chat);

        var result = await workflow.SendAsync(
            conversation.Id,
            "medir esta execução",
            "usage-dashboard");
        var dashboard = await chat.GetDashboardAsync();

        Assert.Equal(1, dashboard.Totals.ExecutionCount);
        Assert.Equal(1, dashboard.Totals.ConversationCount);
        Assert.Equal(result.InputUnits, dashboard.Totals.InputUnits);
        Assert.Equal(result.OutputUnits, dashboard.Totals.OutputUnits);
        Assert.Equal(Aic.FromAic(20), dashboard.Totals.EstimatedCostMinor);
        Assert.Equal(Aic.FromAic(12), dashboard.Totals.ActualCostMinor);
        Assert.Equal(Aic.FromAic(8), dashboard.Totals.ReleasedCostMinor);
        Assert.Equal(result.OperationId, Assert.Single(dashboard.RecentExecutions).OperationId);
    }

    [Fact]
    public async Task PreflightUsesOnlyHistoryBeforeBranchPoint()
    {
        var chat = new InMemoryChatStore();
        var conversation = await chat.CreateConversationAsync();
        var firstOperation = Guid.NewGuid();
        var secondOperation = Guid.NewGuid();
        await chat.SaveCompletedAsync(
            "primeira pergunta",
            "context:first",
            new ChatExecutionResult(
                conversation.Id,
                firstOperation,
                "primeira resposta",
                new AiRoute("test", "test", "v1"),
                Aic.FromAic(2),
                Aic.FromAic(1),
                false),
            CancellationToken.None);
        await chat.SaveCompletedAsync(
            "mensagem que será editada",
            "context:second",
            new ChatExecutionResult(
                conversation.Id,
                secondOperation,
                "continuação que não pode vazar",
                new AiRoute("test", "test", "v1"),
                Aic.FromAic(2),
                Aic.FromAic(1),
                false),
            CancellationToken.None);
        var snapshot = await chat.GetConversationAsync(conversation.Id);
        var branchPoint = snapshot!.Messages.Single(x =>
            x.OperationId == secondOperation && x.Role == ChatMessageRole.User);
        var workflow = NewWorkflow(
            await FundedWalletAsync(),
            new FakeAiProvider(),
            chat);

        var plan = await workflow.PrepareAsync(
            conversation.Id,
            "mensagem editada",
            maximumContextMessages: 16,
            beforeMessageId: branchPoint.Id);

        Assert.NotNull(plan.Context);
        Assert.Equal(2, plan.Context.IncludedMessageCount);
        Assert.Contains(plan.Context.Messages, x => x.Content == "primeira pergunta");
        Assert.Contains(plan.Context.Messages, x => x.Content == "primeira resposta");
        Assert.DoesNotContain(
            plan.Context.Messages,
            x => x.Content.Contains("não pode vazar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkflowSendsFrozenContextPlanToProvider()
    {
        var wallet = await FundedWalletAsync();
        var chat = new InMemoryChatStore();
        var conversation = await chat.CreateConversationAsync();
        await chat.SaveCompletedAsync(
            "contexto anterior",
            "context:frozen",
            new ChatExecutionResult(
                conversation.Id,
                Guid.NewGuid(),
                "resposta anterior",
                new AiRoute("test", "test", "v1"),
                Aic.FromAic(2),
                Aic.FromAic(1),
                false),
            CancellationToken.None);
        var provider = new CapturingProvider();
        var workflow = NewWorkflow(wallet, provider, chat);
        var plan = await workflow.PrepareAsync(
            conversation.Id,
            "nova pergunta",
            maximumContextMessages: 8);

        await workflow.SendAsync(
            conversation.Id,
            plan,
            "context:send");

        Assert.NotNull(provider.LastRequest);
        Assert.Equal(plan.Context?.EstimatedInputTokens, provider.LastRequest.EstimatedInputTokens);
        Assert.Collection(
            provider.LastRequest.Context!,
            message => Assert.Equal("contexto anterior", message.Content),
            message => Assert.Equal("resposta anterior", message.Content));
    }

    [Fact]
    public async Task WorkflowFreezesConversationSettingsAndPresetsAreReusable()
    {
        var wallet = await FundedWalletAsync();
        var chat = new InMemoryChatStore();
        var conversation = await chat.CreateConversationAsync();
        var configured = new ConversationAiSettings(
            "atos-fake-chat-v1",
            "Responda com uma lista curta.",
            0.2m,
            128);
        Assert.True(await chat.UpdateConversationAiSettingsAsync(conversation.Id, configured));
        var preset = await chat.SaveChatPresetAsync("Lista curta", configured);
        var provider = new CapturingProvider();
        var workflow = NewWorkflow(wallet, provider, chat);

        var plan = await workflow.PrepareAsync(conversation.Id, "planejar");
        await chat.UpdateConversationAiSettingsAsync(
            conversation.Id,
            ConversationAiSettings.Default);
        await workflow.SendAsync(conversation.Id, plan, "settings:frozen");

        Assert.Equal(configured.Normalize(), plan.Settings);
        Assert.Equal("Responda com uma lista curta.", provider.LastRequest?.SystemInstruction);
        Assert.Equal(0.2m, provider.LastRequest?.Temperature);
        Assert.Equal(128, provider.LastRequest?.MaximumOutputTokens);
        Assert.Equal(preset, Assert.Single(await chat.ListChatPresetsAsync()));

        var branchPoint = (await chat.GetConversationAsync(conversation.Id))!.Messages
            .Single(x => x.Role == ChatMessageRole.User);
        await chat.UpdateConversationAiSettingsAsync(conversation.Id, configured);
        var branch = await chat.BranchConversationAsync(conversation.Id, branchPoint.Id);
        Assert.Equal(configured.Normalize(), branch.AiSettings);

        Assert.True(await chat.DeleteChatPresetAsync(preset.Id));
        Assert.Empty(await chat.ListChatPresetsAsync());
    }

    [Fact]
    public async Task PromptAndCostLimitsAreEnforcedBeforeReservation()
    {
        var wallet = await FundedWalletAsync();
        var provider = new CountingProvider();
        var chat = new InMemoryChatStore();
        var workflow = new ChatWorkflow(
            wallet,
            new FixedPriceEstimator(),
            new BestAvailableRouteSelector(),
            provider,
            chat,
            new ChatExecutionLimits(5, Aic.FromAic(10)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.SendAsync("mensagem longa", "prompt-limit"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.SendAsync("curta", "cost-limit"));

        Assert.Equal(0, provider.CallCount);
        Assert.Single((await wallet.GetSnapshotAsync()).Transactions);
    }

    [Fact]
    public async Task ProtectedProviderTimesOutAndOpensCircuitAfterRepeatedFailures()
    {
        var waiting = new WaitingProviderClient();
        var timeoutProvider = new ProtectedAiProvider(
            waiting,
            new AiProviderProtectionOptions(
                TimeSpan.FromMilliseconds(20),
                2,
                TimeSpan.FromMinutes(1)),
            TimeProvider.System);
        var request = new AiProviderRequest(
            "timeout",
            new AiRoute("test", "test", "v1"));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            timeoutProvider.ExecuteAsync(request, CancellationToken.None));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            timeoutProvider.ExecuteAsync(request, CancellationToken.None));
        await Assert.ThrowsAsync<AiProviderUnavailableException>(() =>
            timeoutProvider.ExecuteAsync(request, CancellationToken.None));

        Assert.Equal(2, waiting.CallCount);
    }

    [Fact]
    public async Task UserCancellationDoesNotCountAsProviderFailure()
    {
        var client = new CancelOnceProviderClient();
        var provider = new ProtectedAiProvider(
            client,
            new AiProviderProtectionOptions(
                TimeSpan.FromSeconds(5),
                1,
                TimeSpan.FromMinutes(1)),
            TimeProvider.System);
        var request = new AiProviderRequest(
            "cancelar",
            new AiRoute("test", "test", "v1"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ExecuteAsync(request, cancellation.Token));
        var result = await provider.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal("ok", result.Content);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task InvalidProviderCostReleasesReservationWithoutCapture()
    {
        var wallet = await FundedWalletAsync();
        var workflow = NewWorkflow(wallet, new InvalidCostProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.SendAsync("custo inválido", "invalid-provider-cost"));
        var snapshot = await wallet.GetSnapshotAsync();

        Assert.Equal(Aic.FromAic(1_000), snapshot.Balance.AvailableMinor);
        Assert.Equal(0, snapshot.Balance.ReservedMinor);
        Assert.DoesNotContain(
            snapshot.Transactions,
            transaction => transaction.Type == LedgerTransactionType.Capture);
    }

    [Fact]
    public async Task AttachmentsAndClassificationAreFrozenSentAndPersisted()
    {
        var attachments = new InMemoryChatAttachmentService();
        var chat = new InMemoryChatStore(attachments);
        var conversation = await chat.CreateConversationAsync();
        var bytes = Encoding.UTF8.GetBytes("conteúdo auditável");
        await using var stream = new MemoryStream(bytes);
        var draft = await attachments.UploadDraftAsync(
            conversation.Id, "evidence.txt", "text/plain", stream, bytes.Length);
        var provider = new CapturingProvider();
        var workflow = new ChatWorkflow(
            await FundedWalletAsync(),
            new FixedPriceEstimator(),
            new BestAvailableRouteSelector(),
            provider,
            chat,
            attachmentService: attachments);

        var plan = await workflow.PrepareAsync(
            conversation.Id,
            "analise o arquivo",
            attachmentIds: [draft.Id],
            dataClassification: AiDataClassification.Private);
        bytes[0] = 0;

        var result = await workflow.SendAsync(conversation.Id, plan, "attachment-request");
        var saved = await chat.GetConversationAsync(conversation.Id);

        Assert.Equal(AiDataClassification.Private, plan.DataClassification);
        Assert.Equal(AiDataClassification.Private, provider.LastRequest!.DataClassification);
        Assert.Equal("conteúdo auditável", Encoding.UTF8.GetString(provider.LastRequest.Attachments![0].Content));
        Assert.Equal([draft.Id], result.AttachmentIds);
        var savedAttachment = Assert.Single(saved!.Messages[0].Attachments!);
        Assert.Equal(ChatAttachmentStatus.Attached, savedAttachment.Status);
        Assert.Empty(await attachments.ListDraftsAsync(conversation.Id));
    }

    [Fact]
    public async Task AttachmentPolicyRejectsMismatchedMimeAndExtension()
    {
        var attachments = new InMemoryChatAttachmentService();
        await using var stream = new MemoryStream([1, 2, 3]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            attachments.UploadDraftAsync(
                Guid.NewGuid(), "payload.pdf", "image/png", stream, stream.Length));

        Assert.Contains("incompatível", exception.Message);
    }

    [Fact]
    public async Task PrivateClassificationCannotUseExternalProvider()
    {
        var chat = new InMemoryChatStore();
        var conversation = await chat.CreateConversationAsync();
        var provider = new CountingProvider();
        var workflow = new ChatWorkflow(
            await FundedWalletAsync(),
            new FixedPriceEstimator(),
            new ExternalRouteSelector(),
            provider,
            chat);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.PrepareAsync(
                conversation.Id,
                "conteúdo privado",
                dataClassification: AiDataClassification.Private));

        Assert.Contains("não podem sair", exception.Message);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task FeedbackIsUpdatedAggregatedAndIncludedInExports()
    {
        var chat = new InMemoryChatStore();
        var conversation = await chat.CreateConversationAsync();
        var workflow = NewWorkflow(await FundedWalletAsync(), new FakeAiProvider(), chat);
        var plan = await workflow.PrepareAsync(conversation.Id, "explique a etapa oito");
        await workflow.SendAsync(conversation.Id, plan, "feedback-export");
        var completed = await chat.GetConversationAsync(conversation.Id);
        var assistant = Assert.Single(completed!.Messages, x => x.Role == ChatMessageRole.Assistant);

        Assert.True(await chat.SetMessageFeedbackAsync(
            assistant.Id,
            ChatFeedbackRating.Negative,
            "  faltou   um exemplo prático  "));
        var evaluated = await chat.GetConversationAsync(conversation.Id);
        var feedback = evaluated!.Messages.Single(x => x.Id == assistant.Id).Feedback;
        Assert.Equal(ChatFeedbackRating.Negative, feedback?.Rating);
        Assert.Equal("faltou um exemplo prático", feedback?.Reason);

        var dashboard = await chat.GetDashboardAsync();
        Assert.Equal(0, dashboard.Totals.PositiveFeedbackCount);
        Assert.Equal(1, dashboard.Totals.NegativeFeedbackCount);
        Assert.Equal(0m, dashboard.Totals.SatisfactionPercent);

        var exports = new ConversationExportService(chat);
        var markdown = await exports.ExportAsync(conversation.Id, ConversationExportFormat.Markdown);
        var json = await exports.ExportAsync(conversation.Id, ConversationExportFormat.Json);
        var markdownText = Encoding.UTF8.GetString(markdown!.Content);
        Assert.Contains("## Iris", markdownText);
        Assert.Contains("Avaliação: Não útil", markdownText);
        Assert.Contains("faltou um exemplo prático", markdownText);
        Assert.Contains("\"rating\": \"negative\"", Encoding.UTF8.GetString(json!.Content));

        Assert.True(await chat.SetMessageFeedbackAsync(assistant.Id, null));
        Assert.Null((await chat.GetConversationAsync(conversation.Id))!.Messages.Single(x => x.Id == assistant.Id).Feedback);
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
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async Task<AiProviderResult> ExecuteAsync(AiProviderRequest request, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
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

    private sealed class ExternalRouteSelector : IAiRouteSelector
    {
        public Task<AiRoute> SelectAsync(string prompt, CancellationToken cancellationToken) =>
            Task.FromResult(new AiRoute("OpenAI", "external", "v1"));
    }

    private sealed class CapturingProvider : IAiProvider
    {
        public AiProviderRequest? LastRequest { get; private set; }

        public Task<AiProviderResult> ExecuteAsync(
            AiProviderRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new AiProviderResult(
                "resposta nova",
                request.EstimatedInputTokens ?? 1,
                1,
                Aic.FromAic(12)));
        }
    }

    private sealed class CountingStreamingProvider : IAiStreamingProvider
    {
        public int CallCount { get; private set; }

        public Task<AiProviderResult> ExecuteAsync(
            AiProviderRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<AiProviderResult> ExecuteStreamingAsync(
            AiProviderRequest request,
            AiTextDeltaHandler onDelta,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await onDelta("resposta única", cancellationToken);
            return new AiProviderResult(
                "resposta única",
                1,
                1,
                Aic.FromAic(12));
        }
    }

    private sealed class FailOnceProvider : IAiProvider
    {
        public int CallCount { get; private set; }

        public Task<AiProviderResult> ExecuteAsync(
            AiProviderRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
            {
                throw new InvalidOperationException("falha temporária");
            }

            return Task.FromResult(new AiProviderResult(
                "recuperado",
                1,
                1,
                Aic.FromAic(12)));
        }
    }

    private sealed class StreamingWaitingProvider : IAiStreamingProvider
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task<AiProviderResult> ExecuteAsync(
            AiProviderRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<AiProviderResult> ExecuteStreamingAsync(
            AiProviderRequest request,
            AiTextDeltaHandler onDelta,
            CancellationToken cancellationToken)
        {
            await onDelta("parcial", cancellationToken);
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Inalcançável");
        }
    }

    private sealed class WaitingProviderClient : IAiProviderClient
    {
        public int CallCount { get; private set; }

        public async Task<AiProviderResult> ExecuteAsync(
            AiProviderRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Inalcançável");
        }
    }

    private sealed class CancelOnceProviderClient : IAiProviderClient
    {
        public int CallCount { get; private set; }

        public async Task<AiProviderResult> ExecuteAsync(
            AiProviderRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new AiProviderResult("ok", 1, 1, Aic.FromAic(1));
        }
    }

    private sealed class InvalidCostProvider : IAiProvider
    {
        public Task<AiProviderResult> ExecuteAsync(
            AiProviderRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AiProviderResult(
                "inválido",
                1,
                1,
                Aic.FromAic(21)));
    }
}
