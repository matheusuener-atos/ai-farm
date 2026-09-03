using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresFarmAnalyticsQuery(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    TimeProvider timeProvider,
    FarmManifestSigningOptions signingOptions) : IFarmAnalyticsQuery
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

        // Projeção estreita para todos os Jobs das duas janelas; payloads de
        // manifesto/evidência e eventos só são carregados para os Completed,
        // porque a verificação profunda por linha é o que permite às métricas
        // denunciarem adulteração (ADR 0028) — ela não é pré-agregável sem
        // abrir mão da tamper-evidence.
        var lightweight = await db.FarmJobs.AsNoTracking()
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
        var completedIds = lightweight
            .Where(x => x.Status == nameof(FarmJobStatus.Completed))
            .Select(x => x.Id)
            .ToArray();
        var completedRecords = await db.FarmJobs.AsNoTracking()
            .Where(x => completedIds.Contains(x.Id))
            .ToArrayAsync(cancellationToken);
        var eventsByJob = (await db.FarmJobEvents.AsNoTracking()
                .Where(x => completedIds.Contains(x.JobId))
                .OrderBy(x => x.Sequence)
                .ToArrayAsync(cancellationToken))
            .GroupBy(x => x.JobId)
            .ToDictionary(x => x.Key, x => (IReadOnlyCollection<FarmJobEventRecord>)x.ToArray());
        var validityByJob = completedRecords.ToDictionary(
            x => x.Id,
            x => FarmEvidenceSecurity.Verify(
                x,
                eventsByJob.GetValueOrDefault(x.Id) ?? [],
                signingOptions,
                out _).IsValid);
        var jobs = lightweight.Select(x =>
        {
            var status = Enum.Parse<FarmJobStatus>(x.Status);
            var evidenceValid = status != FarmJobStatus.Completed ||
                validityByJob.GetValueOrDefault(x.Id);
            return new FarmAnalyticsJob(
                x.Id,
                x.Workload,
                x.WorkloadVersion,
                status,
                x.ComputeUnitsMilli,
                x.DurationMilliseconds,
                x.WorkerPeakMemoryBytes,
                x.WorkerProcessorTimeMilliseconds,
                x.WorkerEnforcement,
                x.CreatedAtUtc,
                x.FinishedAtUtc,
                evidenceValid);
        }).ToArray();

        var nodeRecord = await db.FarmNodes.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId)
            .OrderByDescending(x => x.ConnectionKind == "Agent")
            .ThenBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
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
