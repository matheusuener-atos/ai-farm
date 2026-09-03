namespace atos.AIFarm.Core.Application;

public enum FarmCapacityBlocker
{
    None,
    EmbeddedSuppressed,
    Paused,
    Offline,
    StaleHeartbeat,
    JobsDisabled,
    AgentVersionMismatch,
    InsufficientProcessors,
    InsufficientMemory,
    Busy
}

public sealed record FarmNodeWorkloadCapacity(
    string WorkloadId,
    string WorkloadVersion,
    bool Eligible,
    FarmCapacityBlocker Blocker);

public sealed record FarmNodeCapacitySnapshot(
    FarmNodeSnapshot Node,
    bool Busy,
    int AvailableLogicalProcessors,
    long AvailableMemoryBytes,
    IReadOnlyList<FarmNodeWorkloadCapacity> Workloads,
    int ActiveJobCount = 0)
{
    public int EligibleWorkloadCount => Workloads.Count(x => x.Eligible);
}

public sealed record FarmWorkloadCapacitySnapshot(
    string WorkloadId,
    string DisplayName,
    string WorkloadVersion,
    int RequiredLogicalProcessors,
    long RequiredMemoryBytes,
    int EligibleNodeCount,
    IReadOnlyDictionary<FarmCapacityBlocker, int> BlockedNodes);

public sealed record FarmCapacityDashboard(
    IReadOnlyList<FarmNodeCapacitySnapshot> Nodes,
    IReadOnlyList<FarmWorkloadCapacitySnapshot> Workloads,
    int QueuedDemandCount,
    DateTimeOffset CalculatedAtUtc);

public interface IFarmCapacityQuery
{
    Task<FarmCapacityDashboard> GetAsync(CancellationToken cancellationToken = default);
}

public static class FarmCapacityEvaluator
{
    public static FarmCapacityBlocker Evaluate(
        FarmNodeSnapshot node,
        FarmWorkloadDefinition definition,
        bool busy,
        bool poolHasAgents) => Evaluate(
            node, definition,
            busy ? 0 : node.AuthorizedLogicalProcessors,
            busy ? 0 : node.AuthorizedMemoryBytes,
            poolHasAgents);

    public static FarmCapacityBlocker Evaluate(
        FarmNodeSnapshot node,
        FarmWorkloadDefinition definition,
        int availableLogicalProcessors,
        long availableMemoryBytes,
        bool poolHasAgents)
    {
        definition.ValidateResources();
        if (poolHasAgents && node.ConnectionKind == FarmNodeConnectionKind.Embedded)
            return FarmCapacityBlocker.EmbeddedSuppressed;
        if (node.Status == FarmNodeStatus.Paused) return FarmCapacityBlocker.Paused;
        if (node.ConnectionKind == FarmNodeConnectionKind.Agent && !node.IsHeartbeatFresh)
            return FarmCapacityBlocker.StaleHeartbeat;
        if (node.Status == FarmNodeStatus.Offline) return FarmCapacityBlocker.Offline;
        if (!node.AcceptJobs) return FarmCapacityBlocker.JobsDisabled;
        if (node.ConnectionKind == FarmNodeConnectionKind.Agent &&
            node.AgentVersion != FarmAgentProtocol.CurrentAgentVersion)
            return FarmCapacityBlocker.AgentVersionMismatch;
        if (node.AuthorizedLogicalProcessors < definition.RequiredLogicalProcessors)
            return FarmCapacityBlocker.InsufficientProcessors;
        if (node.AuthorizedMemoryBytes < definition.RequiredMemoryBytes)
            return FarmCapacityBlocker.InsufficientMemory;
        return availableLogicalProcessors < definition.RequiredLogicalProcessors ||
               availableMemoryBytes < definition.RequiredMemoryBytes
            ? FarmCapacityBlocker.Busy
            : FarmCapacityBlocker.None;
    }
}
