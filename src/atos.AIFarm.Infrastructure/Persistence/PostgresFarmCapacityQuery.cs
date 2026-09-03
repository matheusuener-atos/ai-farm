using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

internal sealed class PostgresFarmCapacityQuery(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    IFarmLabService farmLab,
    TimeProvider timeProvider) : IFarmCapacityQuery
{
    public async Task<FarmCapacityDashboard> GetAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var farm = await farmLab.GetDashboardAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var reservations = await db.FarmJobs.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId && FarmJobLifecycle.ActiveStatuses.Contains(x.Status))
            .GroupBy(x => x.NodeId)
            .Select(group => new
            {
                NodeId = group.Key,
                JobCount = group.Count(),
                LogicalProcessors = group.Sum(x => x.ReservedLogicalProcessors),
                MemoryBytes = group.Sum(x => x.ReservedMemoryBytes)
            }).ToDictionaryAsync(x => x.NodeId, cancellationToken);
        var queued = await db.FarmDemands.AsNoTracking().CountAsync(
            x => x.AccountId == identity.AccountId &&
                 (x.Status == FarmDemandStatus.Queued.ToString() ||
                  x.Status == FarmDemandStatus.Dispatching.ToString() ||
                  x.Status == FarmDemandStatus.Processing.ToString() ||
                  x.Status == FarmDemandStatus.Submitted.ToString()), cancellationToken);
        var hasAgents = farm.Nodes.Any(x => x.ConnectionKind == FarmNodeConnectionKind.Agent);
        var nodes = farm.Nodes.Select(node =>
        {
            reservations.TryGetValue(node.Id, out var reserved);
            var activeJobCount = reserved?.JobCount ?? 0;
            var availableProcessors = Math.Max(0,
                node.AuthorizedLogicalProcessors - (reserved?.LogicalProcessors ?? 0));
            var availableMemory = Math.Max(0,
                node.AuthorizedMemoryBytes - (reserved?.MemoryBytes ?? 0));
            var busy = activeJobCount > 0;
            var workloads = FarmWorkloadCatalog.Definitions.Select(definition =>
            {
                var blocker = FarmCapacityEvaluator.Evaluate(
                    node, definition, availableProcessors, availableMemory, hasAgents);
                return new FarmNodeWorkloadCapacity(
                    definition.Id, definition.Version,
                    blocker == FarmCapacityBlocker.None, blocker);
            }).ToArray();
            return new FarmNodeCapacitySnapshot(
                node, busy, availableProcessors, availableMemory, workloads, activeJobCount);
        }).ToArray();
        var workloadsSummary = FarmWorkloadCatalog.Definitions.Select(definition =>
        {
            var evaluations = nodes.SelectMany(x => x.Workloads)
                .Where(x => x.WorkloadId == definition.Id).ToArray();
            return new FarmWorkloadCapacitySnapshot(
                definition.Id, definition.DisplayName, definition.Version,
                definition.RequiredLogicalProcessors, definition.RequiredMemoryBytes,
                evaluations.Count(x => x.Eligible),
                evaluations.Where(x => !x.Eligible)
                    .GroupBy(x => x.Blocker)
                    .ToDictionary(x => x.Key, x => x.Count()) as IReadOnlyDictionary<FarmCapacityBlocker, int>);
        }).ToArray();
        return new(nodes, workloadsSummary, queued, timeProvider.GetUtcNow());
    }
}
