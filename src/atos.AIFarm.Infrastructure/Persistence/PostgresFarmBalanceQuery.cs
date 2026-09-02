using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresFarmBalanceQuery(
    IDbContextFactory<AIFarmDbContext> dbFactory,
    IAIFarmIdentityAccessor identityAccessor,
    IWalletService walletService) : IFarmBalanceQuery
{
    public async Task<FarmBalanceSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var wallet = await walletService.GetSnapshotAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var jobs = await db.FarmJobs
            .AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId &&
                        x.Status == FarmJobStatus.Completed.ToString())
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                ComputeUnitsMilli = group.Sum(x => x.ComputeUnitsMilli),
                FirstAtUtc = group.Min(x => x.FinishedAtUtc),
                LastAtUtc = group.Max(x => x.FinishedAtUtc)
            })
            .SingleOrDefaultAsync(cancellationToken);

        var generatedEarned = wallet.Transactions
            .Where(x => x.Type == LedgerTransactionType.Reward)
            .SelectMany(x => x.Entries)
            .Where(x => x.Bucket == CreditBucket.Earned &&
                        x.Direction == LedgerDirection.Debit &&
                        x.AccountCode.StartsWith("WALLET:", StringComparison.Ordinal))
            .Sum(x => x.AmountMinor);
        var consumed = wallet.Transactions
            .Where(x => x.Type == LedgerTransactionType.Capture)
            .SelectMany(x => x.Entries)
            .Where(x => x.Direction == LedgerDirection.Debit &&
                        x.AccountCode == "atos:AI_CONSUMPTION")
            .Sum(x => x.AmountMinor);

        return new FarmBalanceSnapshot(
            jobs?.ComputeUnitsMilli ?? 0,
            jobs?.Count ?? 0,
            generatedEarned,
            consumed,
            wallet.Balance.AvailableByBucket.GetValueOrDefault(CreditBucket.Earned),
            wallet.Balance.AvailableByBucket.GetValueOrDefault(CreditBucket.Purchased),
            wallet.Balance.AvailableByBucket.GetValueOrDefault(CreditBucket.Promotional),
            wallet.Balance.AvailableMinor,
            wallet.Balance.ReservedMinor,
            jobs?.FirstAtUtc,
            jobs?.LastAtUtc);
    }
}
