namespace atos.AIFarm.Core.Application;

public enum FarmDemandPriority { Low = 0, Normal = 1, High = 2 }
public enum FarmDemandStatus { Queued, Dispatching, Submitted, Cancelled, Expired, Failed }

public sealed record FarmDemandRequest(
    string WorkloadId,
    string Label,
    string InputText,
    FarmDemandPriority Priority = FarmDemandPriority.Normal,
    int LifetimeMinutes = 1_440)
{
    public (FarmUsefulJobSubmission Submission, FarmDemandPriority Priority, int LifetimeMinutes) Validate()
    {
        if (!Enum.IsDefined(Priority))
            throw new ArgumentOutOfRangeException(nameof(Priority), "Prioridade inválida.");
        if (LifetimeMinutes is < 5 or > 10_080)
            throw new ArgumentOutOfRangeException(nameof(LifetimeMinutes),
                "A validade deve ficar entre 5 minutos e 7 dias.");
        return (new FarmUsefulJobRequest(WorkloadId, Label, InputText).Validate(), Priority, LifetimeMinutes);
    }
}

public sealed record FarmDemandSnapshot(
    Guid Id,
    string WorkloadId,
    string Workload,
    string WorkloadVersion,
    int RequiredLogicalProcessors,
    long RequiredMemoryBytes,
    string Label,
    int InputSizeBytes,
    string InputDigest,
    FarmDemandPriority Priority,
    FarmDemandStatus Status,
    int Attempts,
    DateTimeOffset NextAttemptAtUtc,
    DateTimeOffset ExpiresAtUtc,
    Guid? JobId,
    FarmJobStatus? JobStatus,
    string? Detail,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record FarmDemandDashboard(IReadOnlyList<FarmDemandSnapshot> Demands)
{
    public int QueuedCount => Demands.Count(x => x.Status == FarmDemandStatus.Queued);
    public int InFlightCount => Demands.Count(x => x.Status == FarmDemandStatus.Dispatching);
    public int SubmittedCount => Demands.Count(x => x.Status == FarmDemandStatus.Submitted);
}

public interface IFarmDemandQueueService
{
    Task<FarmDemandDashboard> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<FarmDemandSnapshot> SubmitAsync(FarmDemandRequest request, CancellationToken cancellationToken = default);
    Task<FarmDemandSnapshot> SubmitAsync(FarmDemandRequest request, Guid tokenId, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<FarmDemandSnapshot> CancelAsync(Guid demandId, CancellationToken cancellationToken = default);
}

public interface IFarmDemandDispatcher
{
    Task<int> DispatchDueAsync(CancellationToken cancellationToken = default);
}
