using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

public sealed class PostgresAccountIsolationTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ConversationBranchPersistsLineageAndKeepsSourceUnchanged()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var identity = NewIdentity("branch");
        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            await provider.InitializeAIFarmAsync();
            var store = provider.GetRequiredService<IChatStore>();
            var original = await store.CreateConversationAsync();
            var firstOperation = Guid.NewGuid();
            var secondOperation = Guid.NewGuid();
            await SaveExchangeAsync(
                store,
                original.Id,
                firstOperation,
                "contexto inicial",
                "resposta inicial");
            await SaveExchangeAsync(
                store,
                original.Id,
                secondOperation,
                "mensagem a editar",
                "resposta a preservar");

            var source = await store.GetConversationAsync(original.Id);
            var evaluatedAssistant = source!.Messages.Single(
                x => x.OperationId == firstOperation && x.Role == ChatMessageRole.Assistant);
            Assert.True(await store.SetMessageFeedbackAsync(
                evaluatedAssistant.Id,
                ChatFeedbackRating.Negative,
                "resposta incompleta"));
            source = await store.GetConversationAsync(original.Id);
            var branchPoint = source!.Messages.Single(
                x => x.OperationId == secondOperation &&
                     x.Role == ChatMessageRole.User);
            var branch = await store.BranchConversationAsync(
                original.Id,
                branchPoint.Id);
            var reloadedSource = await store.GetConversationAsync(original.Id);
            var reloadedBranch = await store.GetConversationAsync(branch.Id);
            var branchSummary = (await store.ListConversationsAsync())
                .Single(x => x.Id == branch.Id);

            Assert.Equal(original.Id, reloadedBranch?.ParentConversationId);
            Assert.Equal(branchPoint.Id, reloadedBranch?.BranchedFromMessageId);
            Assert.Equal(2, reloadedBranch?.Messages.Count);
            Assert.Equal(
                ChatFeedbackRating.Negative,
                reloadedBranch?.Messages.Single(x => x.Role == ChatMessageRole.Assistant).Feedback?.Rating);
            Assert.Equal(4, reloadedSource?.Messages.Count);
            Assert.Contains(
                reloadedSource!.Messages,
                x => x.Content == "resposta a preservar");
            Assert.Equal(original.Id, branchSummary.ParentConversationId);
        }
        finally
        {
            await CleanupAsync(connectionString, identity);
        }
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task FeedbackAndConversationExportsAreIsolatedByAccount()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var firstIdentity = NewIdentity("feedback-first");
        var secondIdentity = NewIdentity("feedback-second");
        try
        {
            await using var first = BuildProvider(connectionString, firstIdentity);
            await using var second = BuildProvider(connectionString, secondIdentity);
            await first.InitializeAIFarmAsync();
            await second.InitializeAIFarmAsync();
            var firstStore = first.GetRequiredService<IChatStore>();
            var secondStore = second.GetRequiredService<IChatStore>();
            var conversation = await firstStore.CreateConversationAsync();
            await SaveExchangeAsync(firstStore, conversation.Id, Guid.NewGuid(), "entrada", "saída");
            var assistant = (await firstStore.GetConversationAsync(conversation.Id))!.Messages
                .Single(x => x.Role == ChatMessageRole.Assistant);

            Assert.False(await secondStore.SetMessageFeedbackAsync(
                assistant.Id, ChatFeedbackRating.Positive));
            Assert.True(await firstStore.SetMessageFeedbackAsync(
                assistant.Id, ChatFeedbackRating.Positive));
            Assert.Equal(1, (await first.GetRequiredService<IAiUsageQuery>().GetDashboardAsync()).Totals.PositiveFeedbackCount);
            Assert.Equal(0, (await second.GetRequiredService<IAiUsageQuery>().GetDashboardAsync()).Totals.FeedbackCount);
            Assert.NotNull(await first.GetRequiredService<IConversationExportService>()
                .ExportAsync(conversation.Id, ConversationExportFormat.Json));
            Assert.Null(await second.GetRequiredService<IConversationExportService>()
                .ExportAsync(conversation.Id, ConversationExportFormat.Json));
        }
        finally
        {
            await CleanupAsync(connectionString, firstIdentity, secondIdentity);
        }
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task WalletAndConversationAreIsolatedByAccount()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var firstIdentity = NewIdentity("first");
        var secondIdentity = NewIdentity("second");

        try
        {
            await using var first = BuildProvider(connectionString, firstIdentity);
            await using var second = BuildProvider(connectionString, secondIdentity);
            await first.InitializeAIFarmAsync();
            await second.InitializeAIFarmAsync();

            var firstWorkflow = first.GetRequiredService<ChatWorkflow>();
            await firstWorkflow.SendAsync(
                "mensagem exclusiva da primeira conta",
                $"integration:isolation:{firstIdentity.AccountId:N}");

            var firstWallet = await first
                .GetRequiredService<IWalletService>()
                .GetSnapshotAsync();
            var secondWallet = await second
                .GetRequiredService<IWalletService>()
                .GetSnapshotAsync();
            var firstStore = first.GetRequiredService<IChatStore>();
            var secondStore = second.GetRequiredService<IChatStore>();
            var firstConversation = await firstStore.GetOrCreateCurrentConversationAsync();
            var secondConversation = await secondStore.GetOrCreateCurrentConversationAsync();
            var additionalFirstConversation = await firstStore.CreateConversationAsync();
            var crossAccountLookup = await secondStore.GetConversationAsync(
                additionalFirstConversation.Id);
            var crossAccountRename = await secondStore.RenameConversationAsync(
                firstConversation.Id,
                "tentativa externa");
            var renamed = await firstStore.RenameConversationAsync(
                firstConversation.Id,
                "  Projeto   confidencial  ");
            var titleSearch = await firstStore.SearchConversationsAsync(
                new ConversationQuery("projeto confidencial", 1, 10));
            var messageSearch = await firstStore.SearchConversationsAsync(
                new ConversationQuery("mensagem exclusiva", 1, 10));
            var paged = await firstStore.SearchConversationsAsync(
                new ConversationQuery(Page: 1, PageSize: 1));
            var isolatedSearch = await secondStore.SearchConversationsAsync(
                new ConversationQuery("mensagem exclusiva", 1, 10));
            var literalWildcardSearch = await firstStore.SearchConversationsAsync(
                new ConversationQuery("%", 1, 10));
            var crossAccountArchive = await secondStore.ArchiveConversationAsync(
                firstConversation.Id);
            var archived = await firstStore.ArchiveConversationAsync(firstConversation.Id);
            var archivedLookup = await firstStore.GetConversationAsync(firstConversation.Id);
            var firstSummaries = await firstStore.ListConversationsAsync();
            var secondSummaries = await secondStore.ListConversationsAsync();
            var firstArchivedSummaries =
                await firstStore.ListArchivedConversationsAsync();
            var secondArchivedSummaries =
                await secondStore.ListArchivedConversationsAsync();
            var failureStartedAt = DateTimeOffset.UtcNow;
            await firstStore.SaveFailedAsync(
                $"integration:failure:{firstIdentity.AccountId:N}",
                new FailedChatExecution(
                    additionalFirstConversation.Id,
                    Guid.NewGuid(),
                    new AiRoute("OpenAI", "test-model", "test-catalog-v1"),
                    Aic.FromAic(2),
                    "Timeout",
                    failureStartedAt,
                    failureStartedAt.AddSeconds(30)),
                CancellationToken.None);
            var firstUsage = await first
                .GetRequiredService<IAiUsageQuery>()
                .GetDashboardAsync();
            var secondUsage = await second
                .GetRequiredService<IAiUsageQuery>()
                .GetDashboardAsync();
            var firstFactory = first.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
            await using var verificationDb = await firstFactory.CreateDbContextAsync();
            var archivedRecord = await verificationDb.Conversations
                .SingleAsync(x => x.Id == firstConversation.Id);
            var archivedStatus = archivedRecord.Status;
            var preservedMessageCount = await verificationDb.Messages
                .CountAsync(x => x.ConversationId == firstConversation.Id);
            var crossAccountRestore = await secondStore.RestoreConversationAsync(
                firstConversation.Id);
            var restored = await firstStore.RestoreConversationAsync(firstConversation.Id);
            var restoredConversation = await firstStore.GetConversationAsync(
                firstConversation.Id);
            var activeAfterRestore = await firstStore.ListConversationsAsync();
            var archivedAfterRestore =
                await firstStore.ListArchivedConversationsAsync();
            await verificationDb.Entry(archivedRecord).ReloadAsync();
            var crossAccountDelete = await secondStore.DeleteConversationAsync(
                additionalFirstConversation.Id);
            var deleted = await firstStore.DeleteConversationAsync(
                additionalFirstConversation.Id);
            var deletedLookup = await firstStore.GetConversationAsync(
                additionalFirstConversation.Id);
            var activeAfterDelete = await firstStore.SearchConversationsAsync(
                new ConversationQuery());
            var usageAfterDelete = await first
                .GetRequiredService<IAiUsageQuery>()
                .GetDashboardAsync();
            var deletedRecord = await verificationDb.Conversations
                .SingleAsync(x => x.Id == additionalFirstConversation.Id);
            await verificationDb.Entry(deletedRecord).ReloadAsync();

            Assert.Equal(Aic.FromAic(988), firstWallet.Balance.AvailableMinor);
            Assert.Equal(Aic.FromAic(1_000), secondWallet.Balance.AvailableMinor);
            Assert.Equal(2, firstConversation.Messages.Count);
            Assert.Empty(secondConversation.Messages);
            Assert.NotEqual(firstConversation.Id, secondConversation.Id);
            Assert.Null(crossAccountLookup);
            Assert.False(crossAccountRename);
            Assert.True(renamed);
            Assert.Equal(
                firstConversation.Id,
                Assert.Single(titleSearch.Items).Id);
            Assert.Equal(
                firstConversation.Id,
                Assert.Single(messageSearch.Items).Id);
            Assert.Equal(2, paged.TotalCount);
            Assert.Single(paged.Items);
            Assert.True(paged.HasNextPage);
            Assert.Empty(isolatedSearch.Items);
            Assert.Empty(literalWildcardSearch.Items);
            Assert.False(crossAccountArchive);
            Assert.True(archived);
            Assert.Null(archivedLookup);
            Assert.Equal("Archived", archivedStatus);
            Assert.Equal(2, preservedMessageCount);
            Assert.Equal(additionalFirstConversation.Id, Assert.Single(firstSummaries).Id);
            Assert.Single(secondSummaries);
            Assert.Equal(firstConversation.Id, Assert.Single(firstArchivedSummaries).Id);
            Assert.Equal(
                "Projeto confidencial",
                Assert.Single(firstArchivedSummaries).Title);
            Assert.Empty(secondArchivedSummaries);
            Assert.Equal(2, firstUsage.Totals.ExecutionCount);
            Assert.True(firstUsage.Totals.InputUnits > 0);
            Assert.True(firstUsage.Totals.OutputUnits > 0);
            Assert.Equal(Aic.FromAic(12), firstUsage.Totals.ActualCostMinor);
            Assert.Contains(firstUsage.RecentExecutions, x => x.Status == "Timeout");
            Assert.Empty(secondUsage.RecentExecutions);
            Assert.False(crossAccountRestore);
            Assert.True(restored);
            Assert.Equal(2, restoredConversation?.Messages.Count);
            Assert.Equal(
                "Projeto confidencial",
                activeAfterRestore.Single(
                    x => x.Id == firstConversation.Id).Title);
            Assert.Equal("Active", archivedRecord.Status);
            Assert.Equal(2, activeAfterRestore.Count);
            Assert.Empty(archivedAfterRestore);
            Assert.False(crossAccountDelete);
            Assert.True(deleted);
            Assert.Null(deletedLookup);
            Assert.DoesNotContain(
                activeAfterDelete.Items,
                x => x.Id == additionalFirstConversation.Id);
            Assert.Equal("Deleted", deletedRecord.Status);
            Assert.Equal(firstUsage.Totals, usageAfterDelete.Totals);
            Assert.DoesNotContain(
                secondSummaries,
                conversation => conversation.Id == additionalFirstConversation.Id);
        }
        finally
        {
            await CleanupAsync(connectionString, firstIdentity, secondIdentity);
        }
    }

    private static AIFarmIdentity NewIdentity(string suffix)
    {
        var id = Guid.NewGuid();
        return new AIFarmIdentity(id, id, $"atos isolation {suffix}");
    }

    private static ServiceProvider BuildProvider(
        string connectionString,
        AIFarmIdentity identity)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(connectionString, identity);
        return services.BuildServiceProvider();
    }

    private static Task SaveExchangeAsync(
        IChatStore store,
        Guid conversationId,
        Guid operationId,
        string prompt,
        string response) =>
        store.SaveCompletedAsync(
            prompt,
            $"integration:branch:{operationId:N}",
            new ChatExecutionResult(
                conversationId,
                operationId,
                response,
                new AiRoute("test-provider", "test-model", "test-v1"),
                Aic.FromAic(2),
                Aic.FromAic(1),
                false),
            CancellationToken.None);

    private static async Task CleanupAsync(
        string connectionString,
        params AIFarmIdentity[] identities)
    {
        await using var provider = BuildProvider(connectionString, identities[0]);
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var accountIds = identities.Select(x => x.AccountId).ToArray();
        var walletIds = identities.Select(x => x.WalletId).ToArray();
        var conversationIds = await db.Conversations
            .Where(x => accountIds.Contains(x.AccountId))
            .Select(x => x.Id)
            .ToArrayAsync();
        var requestIds = await db.AiRequests
            .Where(x => accountIds.Contains(x.AccountId))
            .Select(x => x.Id)
            .ToArrayAsync();
        var transactionIds = await db.LedgerTransactions
            .Where(x => walletIds.Contains(x.WalletId))
            .Select(x => x.Id)
            .ToArrayAsync();

        db.MessageFeedbacks.RemoveRange(db.MessageFeedbacks.Where(
            x => accountIds.Contains(x.AccountId)));
        db.ChatAttachments.RemoveRange(db.ChatAttachments.Where(
            x => accountIds.Contains(x.AccountId)));
        await db.SaveChangesAsync();
        db.Messages.RemoveRange(db.Messages.Where(
            x => conversationIds.Contains(x.ConversationId)));
        db.AiExecutions.RemoveRange(db.AiExecutions.Where(
            x => requestIds.Contains(x.AiRequestId)));
        db.AiRequests.RemoveRange(db.AiRequests.Where(
            x => accountIds.Contains(x.AccountId)));
        await db.SaveChangesAsync();

        db.Conversations.RemoveRange(db.Conversations.Where(
            x => accountIds.Contains(x.AccountId) &&
                 x.ParentConversationId != null));
        await db.SaveChangesAsync();

        db.Conversations.RemoveRange(db.Conversations.Where(
            x => accountIds.Contains(x.AccountId)));
        db.BalanceReservations.RemoveRange(db.BalanceReservations.Where(
            x => walletIds.Contains(x.WalletId)));
        db.LedgerEntries.RemoveRange(db.LedgerEntries.Where(
            x => transactionIds.Contains(x.TransactionId)));
        db.LedgerTransactions.RemoveRange(db.LedgerTransactions.Where(
            x => walletIds.Contains(x.WalletId)));
        db.Wallets.RemoveRange(db.Wallets.Where(
            x => walletIds.Contains(x.Id)));
        db.Accounts.RemoveRange(db.Accounts.Where(
            x => accountIds.Contains(x.Id)));
        await db.SaveChangesAsync();
    }

    private static string? ReadLocalVariable(string key)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, ".env.local");
            if (File.Exists(path))
            {
                var prefix = $"{key}=";
                var line = File.ReadLines(path)
                    .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
                return line?[prefix.Length..].Trim().Trim('"', '\'');
            }

            directory = directory.Parent;
        }

        return null;
    }
}
