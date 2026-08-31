using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class AIFarmDbContextFactory : IDesignTimeDbContextFactory<AIFarmDbContext>
{
    public AIFarmDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("DATABASE_URL_UNPOOLED") ??
            Environment.GetEnvironmentVariable("ConnectionStrings__AIFarmMigrations") ??
            ReadLocalUnpooledConnection();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Defina DATABASE_URL_UNPOOLED para executar migrations.");
        }

        var options = new DbContextOptionsBuilder<AIFarmDbContext>()
            .UseNpgsql(NeonPostgresConnection.Normalize(connectionString))
            .Options;
        return new AIFarmDbContext(options);
    }

    private static string? ReadLocalUnpooledConnection()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, ".env.local");
            if (File.Exists(path))
            {
                const string prefix = "DATABASE_URL_UNPOOLED=";
                var line = File.ReadLines(path)
                    .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
                return line?[prefix.Length..].Trim().Trim('"', '\'');
            }

            directory = directory.Parent;
        }

        return null;
    }
}
