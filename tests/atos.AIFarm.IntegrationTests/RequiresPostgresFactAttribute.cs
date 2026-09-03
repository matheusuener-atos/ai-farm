namespace atos.AIFarm.IntegrationTests;

/// <summary>
/// Fact que exige Postgres: sem DATABASE_URL o teste é reportado como pulado,
/// nunca como aprovado. Com CI=true o teste executa mesmo sem banco e falha
/// em <see cref="PostgresTestEnvironment.GetRequiredConnectionString"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresPostgresFactAttribute : FactAttribute
{
    public RequiresPostgresFactAttribute()
    {
        if (!PostgresTestEnvironment.HasConnectionString &&
            !PostgresTestEnvironment.IsContinuousIntegration)
        {
            Skip = "DATABASE_URL não definido; teste de integração Postgres pulado.";
        }
    }
}
