using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

public sealed class PostgresConversationPersistenceTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ConversationSettingsAndAccountPresetSurviveServiceRecreation()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var presetName = $"integration-{Guid.NewGuid():N}";
        Guid presetId = default;
        Guid conversationId = default;
        DateTimeOffset previousUpdatedAtUtc = default;
        ConversationAiSettings? previousSettings = null;
        var configured = new ConversationAiSettings(
            "atos-fake-chat-v1",
            "Responda em português e em três tópicos.",
            0.4m,
            256);

        try
        {
            await using (var firstProvider = BuildProvider(connectionString))
            {
                var store = firstProvider.GetRequiredService<IChatStore>();
                var conversation = await store.GetOrCreateCurrentConversationAsync();
                conversationId = conversation.Id;
                previousUpdatedAtUtc = conversation.UpdatedAtUtc;
                previousSettings = conversation.AiSettings;
                Assert.True(await store.UpdateConversationAiSettingsAsync(conversationId, configured));
                presetId = (await store.SaveChatPresetAsync(presetName, configured)).Id;
            }

            await using var recreatedProvider = BuildProvider(connectionString);
            var recreatedStore = recreatedProvider.GetRequiredService<IChatStore>();
            var restored = await recreatedStore.GetConversationAsync(conversationId);
            var restoredPreset = (await recreatedStore.ListChatPresetsAsync())
                .Single(x => x.Id == presetId);

            Assert.Equal(configured.Normalize(), restored?.AiSettings);
            Assert.Equal(configured.Normalize(), restoredPreset.Settings);
            Assert.Equal(presetName, restoredPreset.Name);
        }
        finally
        {
            await using var cleanupProvider = BuildProvider(connectionString);
            var cleanupStore = cleanupProvider.GetRequiredService<IChatStore>();
            if (presetId != Guid.Empty)
            {
                await cleanupStore.DeleteChatPresetAsync(presetId);
            }
            if (conversationId != Guid.Empty && previousSettings is not null)
            {
                await cleanupStore.UpdateConversationAiSettingsAsync(
                    conversationId,
                    previousSettings);
                var factory = cleanupProvider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                var conversation = await db.Conversations.SingleAsync(x => x.Id == conversationId);
                conversation.UpdatedAtUtc = previousUpdatedAtUtc;
                await db.SaveChangesAsync();
            }
        }
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task CompletedExchangeSurvivesServiceProviderRecreation()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var operationId = Guid.NewGuid();
        var idempotencyKey = $"integration:conversation:{operationId:N}";
        Guid conversationId = default;
        DateTimeOffset previousUpdatedAtUtc = default;
        var removeConversationAfterTest = false;

        try
        {
            await using (var firstProvider = BuildProvider(connectionString))
            {
                await firstProvider.InitializeAIFarmAsync();
                var factory = firstProvider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
                await using (var db = await factory.CreateDbContextAsync())
                {
                    removeConversationAfterTest = !await db.Conversations.AnyAsync(
                        x => x.Status == "Active");
                }
                var store = firstProvider.GetRequiredService<IChatStore>();
                var conversation = await store.GetOrCreateCurrentConversationAsync();
                conversationId = conversation.Id;
                previousUpdatedAtUtc = conversation.UpdatedAtUtc;
                await store.SaveCompletedAsync(
                    "mensagem persistente",
                    idempotencyKey,
                    new ChatExecutionResult(
                        conversation.Id,
                        operationId,
                        "resposta persistente",
                        new AiRoute("test-provider", "test-model", "test-v1"),
                        Aic.FromAic(2),
                        Aic.FromAic(1),
                        false),
                    CancellationToken.None);
            }

            await using var recreatedProvider = BuildProvider(connectionString);
            var recreatedStore = recreatedProvider.GetRequiredService<IChatStore>();
            var restored = await recreatedStore.GetOrCreateCurrentConversationAsync();
            var restoredMessages = restored.Messages
                .Where(x => x.OperationId == operationId)
                .ToArray();

            Assert.Equal(conversationId, restored.Id);
            Assert.Collection(
                restoredMessages,
                user => Assert.Equal("mensagem persistente", user.Content),
                assistant =>
                {
                    Assert.Equal("resposta persistente", assistant.Content);
                    Assert.Equal("test-provider", assistant.Route?.Provider);
                    Assert.Equal(Aic.FromAic(1), assistant.ActualCostMinor);
                });
        }
        finally
        {
            await CleanupAsync(
                connectionString,
                operationId,
                idempotencyKey,
                conversationId,
                previousUpdatedAtUtc,
                removeConversationAfterTest);
        }
    }

    private static ServiceProvider BuildProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(connectionString);
        return services.BuildServiceProvider();
    }

    private static async Task CleanupAsync(
        string connectionString,
        Guid operationId,
        string idempotencyKey,
        Guid conversationId,
        DateTimeOffset previousUpdatedAtUtc,
        bool removeConversation)
    {
        await using var provider = BuildProvider(connectionString);
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var request = await db.AiRequests
            .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey);
        var messages = await db.Messages
            .Where(x => x.OperationId == operationId)
            .ToListAsync();
        db.Messages.RemoveRange(messages);
        if (request is not null)
        {
            var execution = await db.AiExecutions
                .SingleOrDefaultAsync(x => x.AiRequestId == request.Id);
            if (execution is not null)
            {
                db.AiExecutions.Remove(execution);
            }
            db.AiRequests.Remove(request);
        }
        if (conversationId != Guid.Empty)
        {
            var conversation = await db.Conversations
                .SingleOrDefaultAsync(x => x.Id == conversationId);
            if (conversation is not null)
            {
                if (removeConversation)
                {
                    db.Conversations.Remove(conversation);
                }
                else
                {
                    conversation.UpdatedAtUtc = previousUpdatedAtUtc;
                }
            }
        }
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
