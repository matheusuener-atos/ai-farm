using atos.AIFarm.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

public sealed class PostgresReadinessProbeTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ReadinessProbeConnectsToDevelopmentDatabase()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(connectionString);

        await using var provider = services.BuildServiceProvider();
        var probe = provider.GetRequiredService<IAIFarmReadinessProbe>();

        await probe.CheckAsync(CancellationToken.None);
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
