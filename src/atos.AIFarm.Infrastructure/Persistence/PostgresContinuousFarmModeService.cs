using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresContinuousFarmModeService(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    TimeProvider timeProvider) : IContinuousFarmModeService
{
    public async Task<bool> GetAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Accounts.AsNoTracking()
            .Where(x => x.Id == identity.AccountId)
            .Select(x => x.ContinuousFarmEnabled)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task SetAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var updated = await db.Accounts
            .Where(x => x.Id == identity.AccountId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.ContinuousFarmEnabled, enabled),
                cancellationToken);
        if (updated == 0)
        {
            db.Accounts.Add(new AccountRecord
            {
                Id = identity.AccountId,
                DisplayName = identity.DisplayName,
                Status = "Active",
                ContinuousFarmEnabled = enabled,
                CreatedAtUtc = timeProvider.GetUtcNow()
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
