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

public static class FarmAgentProtocol
{
    public const string CurrentAgentVersion = "atos-agent/0.6.0";
}

public static class FarmWorkerEnforcement
{
    public const string WindowsJobObjectCpuAffinity = "WINDOWS_JOB_OBJECT+CPU_AFFINITY";
    public const string WindowsJobObject = "WINDOWS_JOB_OBJECT";
    public const string ProcessMonitorCpuAffinity = "PROCESS_MONITOR+CPU_AFFINITY";
    public const string ProcessMonitor = "PROCESS_MONITOR";

    public static bool IsSupported(string value) => value is
        WindowsJobObjectCpuAffinity or
        WindowsJobObject or
        ProcessMonitorCpuAffinity or
        ProcessMonitor;
}

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
    bool IsHeartbeatFresh,
    bool AcceptJobs,
    int AuthorizedLogicalProcessors,
    long AuthorizedMemoryBytes,
    string? VerifiedProfileHash,
    DateTimeOffset? ProfileVerifiedAtUtc);

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
    string AgentVersion,
    bool AcceptJobs = true,
    int MaxLogicalProcessors = 0,
    long MaxMemoryBytes = 0,
    Guid? ActiveJobId = null);

public sealed record FarmAgentHeartbeatResult(
    Guid NodeId,
    FarmNodeStatus Status,
    bool AcceptJobs,
    DateTimeOffset ServerTimeUtc,
    int HeartbeatIntervalSeconds,
    int ActiveHeartbeatIntervalSeconds,
    Guid? ActiveJobId,
    DateTimeOffset? LeaseExpiresAtUtc,
    bool CancelActiveJob);

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
    long DurationMilliseconds,
    long PeakMemoryBytes = 0,
    long ProcessorTimeMilliseconds = 0,
    string Enforcement = "UNREPORTED");

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
    string DataClassification,
    string? NodeProfileHash);

public sealed record FarmJobEvidenceSnapshot(
    string Version,
    string Algorithm,
    string KeyId,
    string Digest,
    string Signature,
    DateTimeOffset CreatedAtUtc,
    FarmJobEvidenceVerificationSnapshot Verification)
{
    public bool IsValid => Verification.IsValid;
}

public sealed record FarmJobEvidenceVerificationSnapshot(
    bool SignatureValid,
    bool ManifestValid,
    bool JobBindingValid,
    bool LifecycleValid)
{
    public bool IsValid =>
        SignatureValid && ManifestValid && JobBindingValid && LifecycleValid;
}

public sealed record FarmJobEvidenceDownload(
    string FileName,
    byte[] Content);

public sealed record FarmJobSnapshot(
    Guid Id,
    Guid NodeId,
    string Workload,
    string WorkloadVersion,
    string VerificationPolicy,
    FarmJobStatus Status,
    long ComputeUnitsMilli,
    long? DurationMilliseconds,
    string? ResultDigest,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    DateTimeOffset? CancellationRequestedAtUtc,
    DateTimeOffset? AgentLeaseExpiresAtUtc,
    long? WorkerPeakMemoryBytes,
    long? WorkerProcessorTimeMilliseconds,
    string? WorkerEnforcement,
    FarmJobManifestSnapshot? Manifest,
    FarmJobEvidenceSnapshot? Evidence,
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
    Task<FarmJobSnapshot> RunWorkloadAsync(
        string workloadId,
        CancellationToken cancellationToken = default);
    Task<FarmJobSnapshot?> GetJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);
    Task<FarmDashboard> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<FarmJobEvidenceDownload?> DownloadEvidenceAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);
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
