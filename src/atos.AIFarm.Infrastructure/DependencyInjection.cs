using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using atos.AIFarm.Infrastructure.Development;
using atos.AIFarm.Infrastructure.Attachments;
using atos.AIFarm.Infrastructure.Persistence;
using atos.AIFarm.Infrastructure.Providers.OpenAI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace atos.AIFarm.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAIFarmDevelopment(
        this IServiceCollection services,
        ChatExecutionLimits? executionLimits = null,
        AiProviderProtectionOptions? providerProtection = null,
        OpenAiProviderOptions? openAi = null,
        ChatContextLimits? contextLimits = null,
        AiUsageSafetyCapPolicy? safetyCap = null)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton(executionLimits ?? ChatExecutionLimits.Default);
        services.TryAddSingleton(providerProtection ?? AiProviderProtectionOptions.Default);
        services.TryAddSingleton((safetyCap ?? AiUsageSafetyCapPolicy.Disabled).Validate());
        var configuredContextLimits = (contextLimits ?? ChatContextLimits.Default).Validate();
        services.TryAddSingleton(configuredContextLimits);
        services.TryAddSingleton<IChatContextPlanner>(
            new ChatContextPlanner(configuredContextLimits));
        services.TryAddSingleton<IFarmKnowledgeSearchService,
            EmptyFarmKnowledgeSearchService>();
        services.TryAddSingleton<IAIFarmIdentityAccessor>(
            new FixedAIFarmIdentityAccessor(AIFarmIdentity.Development));
        services.AddSingleton<IAIFarmReadinessProbe, InMemoryReadinessProbe>();
        services.AddSingleton<IWalletService, InMemoryWalletService>();
        services.AddScoped<IReservationExpirationService,
            CurrentWalletReservationExpirationService>();
        services.AddSingleton<InMemoryChatStore>();
        services.AddSingleton<InMemoryChatAttachmentService>();
        services.AddSingleton<IChatAttachmentService>(provider =>
            provider.GetRequiredService<InMemoryChatAttachmentService>());
        services.AddSingleton<IChatStore>(provider =>
            provider.GetRequiredService<InMemoryChatStore>());
        services.AddSingleton<IAiUsageQuery>(provider =>
            provider.GetRequiredService<InMemoryChatStore>());
        services.AddSingleton<IAiUsageBudgetService, InMemoryAiUsageBudgetService>();
        services.TryAddScoped<IConversationExportService, ConversationExportService>();
        if (openAi is null)
        {
            services.AddSingleton<IPriceEstimator, FixedPriceEstimator>();
            services.AddSingleton<BestAvailableRouteSelector>();
            services.AddSingleton<IAiRouteSelector>(provider =>
                provider.GetRequiredService<BestAvailableRouteSelector>());
            services.AddSingleton<IAiModelCatalog>(provider =>
                provider.GetRequiredService<BestAvailableRouteSelector>());
            services.AddSingleton<IAiProviderClient, FakeAiProvider>();
        }
        else
        {
            openAi.Validate();
            services.AddSingleton(openAi);
            services.AddSingleton<OpenAiPriceCatalog>();
            services.AddSingleton<IAiPriceCatalog>(provider =>
                provider.GetRequiredService<OpenAiPriceCatalog>());
            services.AddSingleton<IPriceEstimator>(provider =>
                provider.GetRequiredService<OpenAiPriceCatalog>());
            services.AddSingleton<IAiRouteSelector>(provider =>
                provider.GetRequiredService<OpenAiPriceCatalog>());
            services.AddSingleton<IAiModelCatalog>(provider =>
                provider.GetRequiredService<OpenAiPriceCatalog>());
            services.AddSingleton<OpenAiResponsesProvider>();
            services.AddSingleton<IAiProviderClient>(provider =>
                provider.GetRequiredService<OpenAiResponsesProvider>());
        }
        services.AddSingleton<IAiProvider, ProtectedAiProvider>();
        services.AddScoped<ChatWorkflow>();
        services.AddScoped<IrisApiService>();
        services.AddHostedService<ReservationExpirationWorker>();
        return services;
    }

    public static IServiceCollection AddAIFarmPostgres(
        this IServiceCollection services,
        string connectionString,
        AIFarmIdentity? identity = null,
        NeonObjectStorageOptions? objectStorage = null,
        FarmManifestSigningOptions? farmManifestSigning = null,
        FarmNodeReliabilityPolicy? nodeReliabilityPolicy = null,
        FarmRewardPolicy? rewardPolicy = null)
    {
        var normalized = NeonPostgresConnection.Normalize(connectionString);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton(AiUsageSafetyCapPolicy.Disabled);
        services.TryAddSingleton(
            farmManifestSigning ?? FarmManifestSigningOptions.Development);
        services.TryAddSingleton(
            (nodeReliabilityPolicy ?? FarmNodeReliabilityPolicy.Default).Validate());
        services.TryAddSingleton(
            (rewardPolicy ?? FarmRewardPolicy.Disabled).Validate());
        services.AddPooledDbContextFactory<AIFarmDbContext>(
            options => options.UseNpgsql(normalized));
        if (identity is not null)
        {
            services.RemoveAll<IAIFarmIdentityAccessor>();
            services.AddSingleton<IAIFarmIdentityAccessor>(
                new FixedAIFarmIdentityAccessor(identity));
        }
        services.RemoveAll<IWalletService>();
        services.RemoveAll<IChatStore>();
        services.RemoveAll<IAiUsageQuery>();
        services.RemoveAll<IReservationExpirationService>();
        services.RemoveAll<IAIFarmReadinessProbe>();
        services.RemoveAll<IChatAttachmentService>();
        services.RemoveAll<IFarmLabService>();
        services.RemoveAll<IFarmApiJobService>();
        services.RemoveAll<IFarmUsefulJobService>();
        services.RemoveAll<IFarmAgentService>();
        services.RemoveAll<IFarmEconomicsScenarioStore>();
        services.RemoveAll<IFarmBalanceQuery>();
        services.RemoveAll<IFarmRewardProposalStore>();
        services.RemoveAll<IFarmRewardSettlementService>();
        services.RemoveAll<IFarmAnalyticsQuery>();
        services.RemoveAll<IFarmJobHistoryService>();
        services.RemoveAll<IFarmScheduleService>();
        services.RemoveAll<IFarmScheduleDispatcher>();
        services.RemoveAll<IFarmDemandQueueService>();
        services.RemoveAll<IFarmDemandDispatcher>();
        services.RemoveAll<IFarmCapacityQuery>();
        services.RemoveAll<IAuditTrail>();
        services.RemoveAll<IFarmKnowledgeSearchService>();
        services.RemoveAll<IOperationalIncidentService>();
        services.RemoveAll<IAiUsageBudgetService>();
        services.RemoveAll<IApiTokenService>();
        services.RemoveAll<IApiTokenAuthenticator>();
        services.AddScoped<IWalletService, PostgresWalletService>();
        services.AddScoped<IChatStore, PostgresChatStore>();
        services.AddScoped<IAiUsageQuery, PostgresAiUsageQuery>();
        services.AddSingleton<IReservationExpirationService,
            PostgresReservationExpirationService>();
        services.AddSingleton<IAIFarmReadinessProbe, PostgresReadinessProbe>();
        services.AddScoped<PostgresFarmLabService>();
        services.AddScoped<IFarmLabService>(provider => provider.GetRequiredService<PostgresFarmLabService>());
        services.AddScoped<IFarmApiJobService, PostgresFarmApiJobService>();
        services.AddScoped<IFarmUsefulJobService, PostgresFarmUsefulJobService>();
        services.AddScoped<IFarmAgentService, PostgresFarmAgentService>();
        services.AddScoped<IFarmEconomicsScenarioStore, PostgresFarmEconomicsScenarioStore>();
        services.AddScoped<IFarmBalanceQuery, PostgresFarmBalanceQuery>();
        services.AddScoped<IFarmRewardProposalStore, PostgresFarmRewardProposalStore>();
        services.AddScoped<IFarmRewardSettlementService, PostgresFarmRewardSettlementService>();
        services.AddScoped<IFarmAnalyticsQuery, PostgresFarmAnalyticsQuery>();
        services.AddScoped<IFarmJobHistoryService, PostgresFarmJobHistoryService>();
        services.AddScoped<PostgresFarmScheduleService>();
        services.AddScoped<IFarmScheduleService>(provider =>
            provider.GetRequiredService<PostgresFarmScheduleService>());
        services.AddScoped<IFarmScheduleDispatcher>(provider =>
            provider.GetRequiredService<PostgresFarmScheduleService>());
        services.AddHostedService<FarmScheduleWorker>();
        services.AddScoped<PostgresFarmDemandQueueService>();
        services.AddScoped<IFarmDemandQueueService>(provider =>
            provider.GetRequiredService<PostgresFarmDemandQueueService>());
        services.AddScoped<IFarmDemandDispatcher>(provider =>
            provider.GetRequiredService<PostgresFarmDemandQueueService>());
        services.AddHostedService<FarmDemandWorker>();
        services.AddScoped<IFarmCapacityQuery, PostgresFarmCapacityQuery>();
        services.AddScoped<IAuditTrail, PostgresAuditTrail>();
        services.AddScoped<IFarmKnowledgeSearchService, PostgresFarmKnowledgeSearchService>();
        services.AddScoped<IOperationalIncidentService, PostgresOperationalIncidentService>();
        services.AddScoped<IAiUsageBudgetService, PostgresAiUsageBudgetService>();
        services.AddScoped<PostgresApiTokenService>();
        services.AddScoped<IApiTokenService>(provider => provider.GetRequiredService<PostgresApiTokenService>());
        services.AddScoped<IApiTokenAuthenticator>(provider => provider.GetRequiredService<PostgresApiTokenService>());
        if (objectStorage is null)
        {
            services.AddSingleton<IChatAttachmentService, UnavailableChatAttachmentService>();
        }
        else
        {
            objectStorage.Validate();
            services.AddSingleton(objectStorage);
            services.AddSingleton<IChatObjectStorage, NeonS3ObjectStorage>();
            services.AddScoped<IChatAttachmentService, PostgresChatAttachmentService>();
        }
        return services;
    }

    public static async Task InitializeAIFarmAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var wallet = services.GetRequiredService<IWalletService>();
        await wallet.GrantPromotionalAsync(
            Aic.FromAic(1_000),
            "development:promotional-grant:v1",
            cancellationToken);
        var timeProvider = services.GetRequiredService<TimeProvider>();
        await wallet.ExpireReservationsAsync(
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public static async Task MigrateAIFarmAsync(
        this IServiceProvider services,
        string directConnectionString,
        CancellationToken cancellationToken = default)
    {
        _ = services;
        var options = new DbContextOptionsBuilder<AIFarmDbContext>()
            .UseNpgsql(NeonPostgresConnection.Normalize(directConnectionString))
            .Options;
        await using var db = new AIFarmDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
