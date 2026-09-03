using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresInterfaceModeService(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    TimeProvider timeProvider) : IInterfaceModeService
{
    public async Task<AIFarmInterfaceMode> GetAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var stored = await db.Accounts.AsNoTracking()
            .Where(x => x.Id == identity.AccountId)
            .Select(x => x.InterfaceMode)
            .SingleOrDefaultAsync(cancellationToken);
        return Enum.TryParse<AIFarmInterfaceMode>(stored, out var mode)
            ? mode
            : AIFarmInterfaceMode.Lite;
    }

    public async Task SetAsync(
        AIFarmInterfaceMode mode,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var updated = await db.Accounts
            .Where(x => x.Id == identity.AccountId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.InterfaceMode, mode.ToString()),
                cancellationToken);
        if (updated == 0)
        {
            db.Accounts.Add(new AccountRecord
            {
                Id = identity.AccountId,
                DisplayName = identity.DisplayName,
                Status = "Active",
                InterfaceMode = mode.ToString(),
                CreatedAtUtc = timeProvider.GetUtcNow()
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
