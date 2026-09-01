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
        ChatContextLimits? contextLimits = null)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton(executionLimits ?? ChatExecutionLimits.Default);
        services.TryAddSingleton(providerProtection ?? AiProviderProtectionOptions.Default);
        var configuredContextLimits = (contextLimits ?? ChatContextLimits.Default).Validate();
        services.TryAddSingleton(configuredContextLimits);
        services.TryAddSingleton<IChatContextPlanner>(
            new ChatContextPlanner(configuredContextLimits));
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
        services.AddHostedService<ReservationExpirationWorker>();
        return services;
    }

    public static IServiceCollection AddAIFarmPostgres(
        this IServiceCollection services,
        string connectionString,
        AIFarmIdentity? identity = null,
        NeonObjectStorageOptions? objectStorage = null,
        FarmManifestSigningOptions? farmManifestSigning = null)
    {
        var normalized = NeonPostgresConnection.Normalize(connectionString);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton(
            farmManifestSigning ?? FarmManifestSigningOptions.Development);
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
        services.AddScoped<IWalletService, PostgresWalletService>();
        services.AddScoped<IChatStore, PostgresChatStore>();
        services.AddScoped<IAiUsageQuery, PostgresAiUsageQuery>();
        services.AddSingleton<IReservationExpirationService,
            PostgresReservationExpirationService>();
        services.AddSingleton<IAIFarmReadinessProbe, PostgresReadinessProbe>();
        services.AddScoped<IFarmLabService, PostgresFarmLabService>();
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
