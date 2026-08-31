using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using atos.AIFarm.Infrastructure.Development;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace atos.AIFarm.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAIFarmDevelopment(this IServiceCollection services)
    {
        services.AddSingleton<IWalletService, InMemoryWalletService>();
        services.AddSingleton<IChatStore, InMemoryChatStore>();
        services.AddSingleton<IPriceEstimator, FixedPriceEstimator>();
        services.AddSingleton<IAiRouteSelector, BestAvailableRouteSelector>();
        services.AddSingleton<IAiProvider, FakeAiProvider>();
        services.AddSingleton<ChatWorkflow>();
        return services;
    }

    public static IServiceCollection AddAIFarmPostgres(
        this IServiceCollection services,
        string connectionString)
    {
        var normalized = NeonPostgresConnection.Normalize(connectionString);
        services.AddPooledDbContextFactory<AIFarmDbContext>(
            options => options.UseNpgsql(normalized));
        services.RemoveAll<IWalletService>();
        services.RemoveAll<IChatStore>();
        services.AddSingleton<IWalletService, PostgresWalletService>();
        services.AddSingleton<IChatStore, PostgresChatStore>();
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
