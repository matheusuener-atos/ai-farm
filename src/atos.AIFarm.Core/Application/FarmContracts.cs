namespace atos.AIFarm.Core.Application;

public enum FarmNodeStatus { Online, Paused }
public enum FarmVerificationLevel { Detected, Verified }
public enum FarmJobStatus { Queued, Running, Verifying, Completed, Failed }

public sealed record FarmNodeSnapshot(
    Guid Id,
    string Name,
    FarmNodeStatus Status,
    FarmVerificationLevel VerificationLevel,
    string Runtime,
    int LogicalProcessors,
    long MemoryBytes,
    DateTimeOffset LastSeenAtUtc);

public sealed record FarmJobEventSnapshot(
    int Sequence,
    FarmJobStatus Status,
    DateTimeOffset OccurredAtUtc,
    string Detail);

public sealed record FarmJobSnapshot(
    Guid Id,
    Guid NodeId,
    string Workload,
    string WorkloadVersion,
    string VerificationPolicy,
    FarmJobStatus Status,
    long ComputeUnitsMilli,
    string? ResultDigest,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    IReadOnlyList<FarmJobEventSnapshot> Events);

public sealed record FarmDashboard(
    FarmNodeSnapshot Node,
    IReadOnlyList<FarmJobSnapshot> RecentJobs)
{
    public long VerifiedComputeUnitsMilli =>
        RecentJobs.Where(x => x.Status == FarmJobStatus.Completed)
            .Sum(x => x.ComputeUnitsMilli);
}

public interface IFarmLabService
{
    Task<FarmDashboard> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<FarmDashboard> SetNodeStatusAsync(FarmNodeStatus status, CancellationToken cancellationToken = default);
    Task<FarmJobSnapshot> RunBenchmarkAsync(CancellationToken cancellationToken = default);
}
