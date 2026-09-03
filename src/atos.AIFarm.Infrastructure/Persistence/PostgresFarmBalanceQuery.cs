using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresFarmBalanceQuery(
    IDbContextFactory<AIFarmDbContext> dbFactory,
    IAIFarmIdentityAccessor identityAccessor,
    IWalletService walletService,
    FarmManifestSigningOptions signingOptions) : IFarmBalanceQuery
{
    public async Task<FarmBalanceSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var wallet = await walletService.GetSnapshotAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var completedJobs = await db.FarmJobs
            .AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId &&
                        x.Status == FarmJobStatus.Completed.ToString())
            .ToArrayAsync(cancellationToken);
        var jobIds = completedJobs.Select(x => x.Id).ToArray();
        var eventsByJob = (await db.FarmJobEvents.AsNoTracking()
                .Where(x => jobIds.Contains(x.JobId))
                .OrderBy(x => x.Sequence)
                .ToArrayAsync(cancellationToken))
            .GroupBy(x => x.JobId)
            .ToDictionary(x => x.Key, x => (IReadOnlyCollection<FarmJobEventRecord>)x.ToArray());
        var verifiedJobs = completedJobs.Where(job =>
            FarmEvidenceSecurity.Verify(
                job,
                eventsByJob.GetValueOrDefault(job.Id) ?? [],
                signingOptions,
                out _).IsValid).ToArray();

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
            verifiedJobs.Sum(x => x.ComputeUnitsMilli),
            verifiedJobs.Length,
            generatedEarned,
            consumed,
            wallet.Balance.AvailableByBucket.GetValueOrDefault(CreditBucket.Earned),
            wallet.Balance.AvailableByBucket.GetValueOrDefault(CreditBucket.Purchased),
            wallet.Balance.AvailableByBucket.GetValueOrDefault(CreditBucket.Promotional),
            wallet.Balance.AvailableMinor,
            wallet.Balance.ReservedMinor,
            verifiedJobs.Select(x => x.FinishedAtUtc).Min(),
            verifiedJobs.Select(x => x.FinishedAtUtc).Max());
    }
}
