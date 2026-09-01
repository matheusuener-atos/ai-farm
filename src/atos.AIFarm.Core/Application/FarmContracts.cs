namespace atos.AIFarm.Core.Application;

public enum FarmNodeStatus { Online, Paused, Offline }
public enum FarmVerificationLevel { Detected, Verified }
public enum FarmJobStatus
{
    Queued,
    Offered,
    Accepted,
    Running,
    Uploading,
    Verifying,
    Completed,
    Failed,
    Cancelled,
    TimedOut,
    Rejected
}
public enum FarmNodeConnectionKind { Embedded, Agent }
public enum FarmRemoteJobRejectionKind { Policy, Timeout, Cancelled }

public sealed record FarmNodeSnapshot(
    Guid Id,
    string Name,
    FarmNodeStatus Status,
    FarmVerificationLevel VerificationLevel,
    string Runtime,
    int LogicalProcessors,
    long MemoryBytes,
    DateTimeOffset LastSeenAtUtc,
    FarmNodeConnectionKind ConnectionKind,
    string? AgentVersion,
    bool IsHeartbeatFresh);

public sealed record FarmAgentEnrollment(
    Guid Id,
    string Token,
    DateTimeOffset ExpiresAtUtc);

public sealed record FarmAgentEnrollmentRequest(
    string Name,
    string Runtime,
    int LogicalProcessors,
    long MemoryBytes,
    string AgentVersion);

public sealed record FarmAgentEnrollmentResult(
    Guid NodeId,
    string Credential,
    int HeartbeatIntervalSeconds);

public sealed record FarmAgentHeartbeatRequest(
    string Runtime,
    int LogicalProcessors,
    long MemoryBytes,
    string AgentVersion);

public sealed record FarmAgentHeartbeatResult(
    Guid NodeId,
    FarmNodeStatus Status,
    bool AcceptJobs,
    DateTimeOffset ServerTimeUtc,
    int HeartbeatIntervalSeconds);

public sealed record FarmAgentTrust(
    string Algorithm,
    string VerificationKey,
    string KeyId);

public sealed record FarmRemoteJobOffer(
    Guid JobId,
    Guid AttemptId,
    string ManifestPayload,
    string ManifestDigest,
    string ManifestSignature,
    string SignatureAlgorithm);

public sealed record FarmRemoteJobResult(
    string ResultDigest,
    long DurationMilliseconds);

public sealed record FarmRemoteJobRejection(
    string Reason,
    FarmRemoteJobRejectionKind Kind = FarmRemoteJobRejectionKind.Policy);

public sealed record FarmRemoteJobAcknowledgement(
    Guid JobId,
    FarmJobStatus Status,
    bool Verified,
    long ComputeUnitsMilli);

public sealed record FarmJobEventSnapshot(
    int Sequence,
    FarmJobStatus Status,
    DateTimeOffset OccurredAtUtc,
    string Detail);

public sealed record FarmJobManifestSnapshot(
    string Version,
    Guid AttemptId,
    string Digest,
    string Signature,
    bool IsSignatureValid,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int TimeoutSeconds,
    int Iterations,
    int MaxLogicalProcessors,
    long MaxMemoryBytes,
    string NetworkPolicy,
    string DataClassification);

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
    FarmJobManifestSnapshot? Manifest,
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
    Task<FarmDashboard> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}

public interface IFarmAgentService
{
    Task<FarmAgentEnrollment> CreateEnrollmentAsync(CancellationToken cancellationToken = default);
    Task<FarmAgentEnrollmentResult?> EnrollAsync(
        string token,
        FarmAgentEnrollmentRequest request,
        CancellationToken cancellationToken = default);
    Task<FarmAgentHeartbeatResult?> HeartbeatAsync(
        string credential,
        FarmAgentHeartbeatRequest request,
        CancellationToken cancellationToken = default);
    Task<FarmAgentTrust?> GetTrustAsync(
        string credential,
        CancellationToken cancellationToken = default);
    Task<FarmRemoteJobOffer?> ClaimJobAsync(
        string credential,
        CancellationToken cancellationToken = default);
    Task<bool> MarkJobStartedAsync(
        string credential,
        Guid jobId,
        CancellationToken cancellationToken = default);
    Task<bool> RejectJobAsync(
        string credential,
        Guid jobId,
        FarmRemoteJobRejection rejection,
        CancellationToken cancellationToken = default);
    Task<FarmRemoteJobAcknowledgement?> SubmitResultAsync(
        string credential,
        Guid jobId,
        FarmRemoteJobResult result,
        CancellationToken cancellationToken = default);
}
