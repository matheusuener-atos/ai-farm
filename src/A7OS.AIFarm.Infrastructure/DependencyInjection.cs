using A7OS.AIFarm.Core.Application;
using A7OS.AIFarm.Core.Wallet;
using A7OS.AIFarm.Infrastructure.Development;
using A7OS.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace A7OS.AIFarm.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAIFarmDevelopment(this IServiceCollection services)
    {
        services.AddSingleton<IWalletService, InMemoryWalletService>();
        services.AddSingleton<IChatResultStore, InMemoryChatResultStore>();
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
        services.AddDbContext<AIFarmDbContext>(options => options.UseNpgsql(connectionString));
        return services;
    }

    public static async Task SeedDevelopmentWalletAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var wallet = services.GetRequiredService<IWalletService>();
        await wallet.GrantPromotionalAsync(
            Aic.FromAic(1_000),
            "development:promotional-grant:v1",
            cancellationToken);
    }
}
