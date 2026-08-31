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
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IWalletService, InMemoryWalletService>();
        services.AddSingleton<IChatStore, InMemoryChatStore>();
        services.AddSingleton<IPriceEstimator, FixedPriceEstimator>();
        services.AddSingleton<IAiRouteSelector, BestAvailableRouteSelector>();
        services.AddSingleton<IAiProvider, FakeAiProvider>();
        services.AddSingleton<ChatWorkflow>();
        services.AddHostedService<ReservationExpirationWorker>();
        return services;
    }

    public static IServiceCollection AddAIFarmPostgres(
        this IServiceCollection services,
        string connectionString,
        AIFarmIdentity? identity = null)
    {
        var normalized = NeonPostgresConnection.Normalize(connectionString);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddPooledDbContextFactory<AIFarmDbContext>(
            options => options.UseNpgsql(normalized));
        services.TryAddSingleton(identity ?? AIFarmIdentity.Development);
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
