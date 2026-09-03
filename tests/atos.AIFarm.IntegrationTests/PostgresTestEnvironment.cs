namespace atos.AIFarm.IntegrationTests;

/// <summary>
/// Resolve a conexão Postgres dos testes de integração: primeiro a variável
/// de ambiente DATABASE_URL, depois o .env.local mais próximo do binário.
/// </summary>
internal static class PostgresTestEnvironment
{
    public static bool IsContinuousIntegration
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("CI");
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                value == "1";
        }
    }

    public static bool HasConnectionString =>
        !string.IsNullOrWhiteSpace(ReadConnectionString());

    public static string GetRequiredConnectionString()
    {
        var value = ReadConnectionString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "DATABASE_URL não definido: exporte a variável de ambiente ou " +
                "preencha .env.local. Com CI=true os testes de integração " +
                "Postgres são obrigatórios e a ausência do banco é falha.");
        }
        return value;
    }

    private static string? ReadConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, ".env.local");
            if (File.Exists(path))
            {
                const string prefix = "DATABASE_URL=";
                var line = File.ReadLines(path)
                    .FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
                return line?[prefix.Length..].Trim().Trim('"', '\'');
            }
            directory = directory.Parent;
        }
        return null;
    }
}
