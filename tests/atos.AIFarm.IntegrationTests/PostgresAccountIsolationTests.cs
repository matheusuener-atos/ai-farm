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
            var firstSummaries = await firstStore.ListConversationsAsync();
            var secondSummaries = await secondStore.ListConversationsAsync();

            Assert.Equal(Aic.FromAic(988), firstWallet.Balance.AvailableMinor);
            Assert.Equal(Aic.FromAic(1_000), secondWallet.Balance.AvailableMinor);
            Assert.Equal(2, firstConversation.Messages.Count);
            Assert.Empty(secondConversation.Messages);
            Assert.NotEqual(firstConversation.Id, secondConversation.Id);
            Assert.Null(crossAccountLookup);
            Assert.Equal(2, firstSummaries.Count);
            Assert.Single(secondSummaries);
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

        db.Messages.RemoveRange(db.Messages.Where(
            x => conversationIds.Contains(x.ConversationId)));
        db.AiExecutions.RemoveRange(db.AiExecutions.Where(
            x => requestIds.Contains(x.AiRequestId)));
        db.AiRequests.RemoveRange(db.AiRequests.Where(
            x => accountIds.Contains(x.AccountId)));
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
