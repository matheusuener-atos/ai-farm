using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure;

public interface IAIFarmReadinessProbe
{
    Task CheckAsync(CancellationToken cancellationToken = default);
}

internal sealed class InMemoryReadinessProbe : IAIFarmReadinessProbe
{
    public Task CheckAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class PostgresReadinessProbe(
    IDbContextFactory<AIFarmDbContext> dbContextFactory) : IAIFarmReadinessProbe
{
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            throw new InvalidOperationException("The PostgreSQL dependency is unavailable.");
        }
    }
}
