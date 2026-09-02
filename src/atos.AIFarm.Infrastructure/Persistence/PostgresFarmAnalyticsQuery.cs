using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresFarmAnalyticsQuery(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    TimeProvider timeProvider) : IFarmAnalyticsQuery
{
    private static readonly TimeSpan HeartbeatTolerance = TimeSpan.FromSeconds(90);

    public async Task<FarmAnalytics> GetAsync(
        int days = 30,
        CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 365)
            throw new ArgumentOutOfRangeException(nameof(days), "O período deve ficar entre 1 e 365 dias.");

        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var earliest = today.AddDays(-(days * 2 - 1));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var records = await db.FarmJobs.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId && x.CreatedAtUtc >= earliest)
            .Select(x => new
            {
                x.Id,
                x.Workload,
                x.WorkloadVersion,
                x.Status,
                x.ComputeUnitsMilli,
                x.DurationMilliseconds,
                x.WorkerPeakMemoryBytes,
                x.WorkerProcessorTimeMilliseconds,
                x.WorkerEnforcement,
                x.CreatedAtUtc,
                x.FinishedAtUtc
            })
            .ToArrayAsync(cancellationToken);
        var jobs = records.Select(x => new FarmAnalyticsJob(
            x.Id,
            x.Workload,
            x.WorkloadVersion,
            Enum.Parse<FarmJobStatus>(x.Status),
            x.ComputeUnitsMilli,
            x.DurationMilliseconds,
            x.WorkerPeakMemoryBytes,
            x.WorkerProcessorTimeMilliseconds,
            x.WorkerEnforcement,
            x.CreatedAtUtc,
            x.FinishedAtUtc)).ToArray();

        var nodeRecord = await db.FarmNodes.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AccountId == identity.AccountId, cancellationToken);
        FarmAnalyticsNode? node = null;
        if (nodeRecord is not null)
        {
            var connectionKind = Enum.Parse<FarmNodeConnectionKind>(nodeRecord.ConnectionKind);
            var fresh = connectionKind == FarmNodeConnectionKind.Embedded ||
                now - nodeRecord.LastSeenAtUtc <= HeartbeatTolerance;
            var status = fresh
                ? Enum.Parse<FarmNodeStatus>(nodeRecord.Status)
                : FarmNodeStatus.Offline;
            node = new FarmAnalyticsNode(
                nodeRecord.Id,
                nodeRecord.Name,
                status,
                Enum.Parse<FarmVerificationLevel>(nodeRecord.VerificationLevel),
                connectionKind,
                fresh,
                fresh && status == FarmNodeStatus.Online && nodeRecord.AgentAcceptJobs,
                nodeRecord.LogicalProcessors,
                nodeRecord.MemoryBytes,
                nodeRecord.LastSeenAtUtc,
                nodeRecord.AgentVersion);
        }

        var scenarioCount = await db.FarmEconomicsScenarios.AsNoTracking()
            .CountAsync(x => x.AccountId == identity.AccountId, cancellationToken);
        var reward = await db.FarmRewardProposals.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                Points = group.Sum(x => x.ProcessingPointsMilli),
                Reward = group.Sum(x => x.RecommendedRewardBrl)
            })
            .SingleOrDefaultAsync(cancellationToken);
        var economics = new FarmEconomicsPipelineAnalytics(
            scenarioCount,
            reward?.Count ?? 0,
            reward?.Points ?? 0,
            reward?.Reward ?? 0);

        return FarmAnalyticsBuilder.Build(jobs, node, economics, days, now);
    }
}
