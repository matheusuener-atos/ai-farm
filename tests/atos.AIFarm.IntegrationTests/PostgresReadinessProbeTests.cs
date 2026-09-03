using atos.AIFarm.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

public sealed class PostgresReadinessProbeTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ReadinessProbeConnectsToDevelopmentDatabase()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(connectionString);

        await using var provider = services.BuildServiceProvider();
        var probe = provider.GetRequiredService<IAIFarmReadinessProbe>();

        await probe.CheckAsync(CancellationToken.None);
    }

}
