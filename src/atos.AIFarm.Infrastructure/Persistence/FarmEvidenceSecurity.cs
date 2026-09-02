using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

internal sealed record FarmEvidenceEvent(
    int Sequence,
    string Status,
    DateTimeOffset OccurredAtUtc,
    string Detail);

internal sealed record FarmJobEvidencePayload(
    string Version,
    string Algorithm,
    string KeyId,
    Guid JobId,
    Guid AttemptId,
    Guid NodeId,
    string Workload,
    string WorkloadVersion,
    string VerificationPolicy,
    string ManifestVersion,
    string ManifestDigest,
    string ManifestSignature,
    string ManifestPayload,
    string ResultDigest,
    string ExpectedDigest,
    long ComputeUnitsMilli,
    long DurationMilliseconds,
    long? WorkerPeakMemoryBytes,
    long? WorkerProcessorTimeMilliseconds,
    string? WorkerEnforcement,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    IReadOnlyList<FarmEvidenceEvent> Events);

internal sealed record SignedFarmJobEvidence(
    FarmJobEvidencePayload Evidence,
    string Payload,
    string Digest,
    string Signature);

internal sealed record FarmJobEvidenceEnvelope(
    string Algorithm,
    string KeyId,
    string PayloadDigest,
    string Signature,
    FarmJobEvidencePayload Evidence);

internal static class FarmEvidenceSecurity
{
    public const string Version = "atos-farm-evidence-v1";
    public const string Algorithm = "HMAC-SHA256-NODE-V1";

    public static SignedFarmJobEvidence Sign(
        FarmJobRecord job,
        IReadOnlyList<FarmJobEventRecord> events,
        FarmManifestSigningOptions options,
        string keyId)
    {
        if (job.Status != "Completed" ||
            job.AttemptId is null ||
            job.ManifestVersion is null ||
            job.ManifestDigest is null ||
            job.ManifestSignature is null ||
            job.ManifestPayload is null ||
            job.ResultDigest is null ||
            job.ExpectedDigest is null ||
            job.DurationMilliseconds is null ||
            job.FinishedAtUtc is null)
            throw new InvalidOperationException("O Job ainda não possui dados completos para emitir evidência.");

        var evidence = new FarmJobEvidencePayload(
            Version,
            Algorithm,
            keyId,
            job.Id,
            job.AttemptId.Value,
            job.NodeId,
            job.Workload,
            job.WorkloadVersion,
            job.VerificationPolicy,
            job.ManifestVersion,
            job.ManifestDigest,
            job.ManifestSignature,
            job.ManifestPayload,
            job.ResultDigest,
            job.ExpectedDigest,
            job.ComputeUnitsMilli,
            job.DurationMilliseconds.Value,
            job.WorkerPeakMemoryBytes,
            job.WorkerProcessorTimeMilliseconds,
            job.WorkerEnforcement,
            job.CreatedAtUtc,
            job.StartedAtUtc,
            job.FinishedAtUtc.Value,
            events.OrderBy(x => x.Sequence).Select(x => new FarmEvidenceEvent(
                x.Sequence,
                x.Status,
                x.OccurredAtUtc,
                x.Detail)).ToArray());
        var payload = JsonSerializer.Serialize(evidence, CanonicalOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);
        return new SignedFarmJobEvidence(
            evidence,
            payload,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Convert.ToHexString(HMACSHA256.HashData(options.Key, bytes)).ToLowerInvariant());
    }

    public static async Task SealAsync(
        AIFarmDbContext db,
        FarmJobRecord job,
        FarmManifestSigningOptions rootOptions,
        CancellationToken cancellationToken)
    {
        if (job.EvidencePayload is not null) return;
        var events = await db.FarmJobEvents
            .Where(x => x.JobId == job.Id)
            .OrderBy(x => x.Sequence)
            .ToArrayAsync(cancellationToken);
        var key = rootOptions.DeriveForAgent(job.NodeId);
        var signed = Sign(job, events, key.Signing, key.KeyId);
        job.EvidenceVersion = Version;
        job.EvidencePayload = signed.Payload;
        job.EvidenceDigest = signed.Digest;
        job.EvidenceSignature = signed.Signature;
        job.EvidenceKeyId = key.KeyId;
        job.EvidenceCreatedAtUtc = job.FinishedAtUtc;
    }

    public static FarmJobEvidenceVerificationSnapshot Verify(
        FarmJobRecord job,
        IReadOnlyCollection<FarmJobEventRecord> currentEvents,
        FarmManifestSigningOptions rootOptions,
        out FarmJobEvidencePayload? evidence)
    {
        evidence = null;
        var key = rootOptions.DeriveForAgent(job.NodeId);
        var signatureValid = job.EvidenceVersion == Version &&
            job.EvidenceKeyId == key.KeyId &&
            TryVerify(
                job.EvidencePayload,
                job.EvidenceDigest,
                job.EvidenceSignature,
                key.Signing,
                out evidence) &&
            evidence is not null &&
            evidence.Algorithm == Algorithm &&
            evidence.KeyId == key.KeyId;
        var manifestValid = FarmManifestSecurity.TryVerifyJobBinding(
            job,
            rootOptions,
            out _);
        var jobBindingValid = signatureValid && evidence is not null &&
            EvidenceMatchesJob(evidence, job);
        var lifecycleValid = signatureValid && evidence is not null &&
            LifecycleMatches(evidence.Events, currentEvents, job);
        return new FarmJobEvidenceVerificationSnapshot(
            signatureValid,
            manifestValid,
            jobBindingValid,
            lifecycleValid);
    }

    private static bool EvidenceMatchesJob(
        FarmJobEvidencePayload evidence,
        FarmJobRecord job) =>
            job.Status == FarmJobStatus.Completed.ToString() &&
            evidence.Version == job.EvidenceVersion &&
            evidence.JobId == job.Id &&
            evidence.NodeId == job.NodeId &&
            evidence.AttemptId == job.AttemptId &&
            evidence.Workload == job.Workload &&
            evidence.WorkloadVersion == job.WorkloadVersion &&
            evidence.VerificationPolicy == job.VerificationPolicy &&
            evidence.ManifestVersion == job.ManifestVersion &&
            evidence.ManifestDigest == job.ManifestDigest &&
            evidence.ManifestSignature == job.ManifestSignature &&
            evidence.ManifestPayload == job.ManifestPayload &&
            evidence.ResultDigest == job.ResultDigest &&
            evidence.ExpectedDigest == job.ExpectedDigest &&
            evidence.ComputeUnitsMilli == job.ComputeUnitsMilli &&
            evidence.DurationMilliseconds == job.DurationMilliseconds &&
            evidence.WorkerPeakMemoryBytes == job.WorkerPeakMemoryBytes &&
            evidence.WorkerProcessorTimeMilliseconds == job.WorkerProcessorTimeMilliseconds &&
            evidence.WorkerEnforcement == job.WorkerEnforcement &&
            SamePostgresTimestamp(evidence.CreatedAtUtc, job.CreatedAtUtc) &&
            SameNullablePostgresTimestamp(evidence.StartedAtUtc, job.StartedAtUtc) &&
            SamePostgresTimestamp(evidence.FinishedAtUtc, job.FinishedAtUtc);

    private static bool LifecycleMatches(
        IReadOnlyList<FarmEvidenceEvent> sealedEvents,
        IReadOnlyCollection<FarmJobEventRecord> currentEvents,
        FarmJobRecord job)
    {
        var ordered = currentEvents.OrderBy(x => x.Sequence).ToArray();
        if (sealedEvents.Count == 0 || sealedEvents.Count != ordered.Length)
            return false;
        for (var index = 0; index < ordered.Length; index++)
        {
            var current = ordered[index];
            var sealedEvent = sealedEvents[index];
            if (current.JobId != job.Id ||
                current.Sequence != index + 1 ||
                sealedEvent.Sequence != current.Sequence ||
                sealedEvent.Status != current.Status ||
                sealedEvent.Detail != current.Detail ||
                !SamePostgresTimestamp(sealedEvent.OccurredAtUtc, current.OccurredAtUtc))
                return false;
        }

        var finalEvent = ordered[^1];
        return finalEvent.Status == FarmJobStatus.Completed.ToString() &&
            SamePostgresTimestamp(finalEvent.OccurredAtUtc, job.FinishedAtUtc);
    }

    public static FarmJobEvidenceSnapshot? ToSnapshot(
        FarmJobRecord job,
        IReadOnlyCollection<FarmJobEventRecord> currentEvents,
        FarmManifestSigningOptions rootOptions)
    {
        if (job.EvidenceVersion is null ||
            job.EvidenceDigest is null ||
            job.EvidenceSignature is null ||
            job.EvidenceKeyId is null ||
            job.EvidenceCreatedAtUtc is null)
            return null;
        var verification = Verify(job, currentEvents, rootOptions, out _);
        return new FarmJobEvidenceSnapshot(
            job.EvidenceVersion,
            Algorithm,
            job.EvidenceKeyId,
            job.EvidenceDigest,
            job.EvidenceSignature,
            job.EvidenceCreatedAtUtc.Value,
            verification);
    }

    public static bool TryVerify(
        string? payload,
        string? digest,
        string? signature,
        FarmManifestSigningOptions options,
        out FarmJobEvidencePayload? evidence)
    {
        evidence = null;
        if (string.IsNullOrWhiteSpace(payload) ||
            !TryHex(digest, out var suppliedDigest) || suppliedDigest.Length != 32 ||
            !TryHex(signature, out var suppliedSignature) || suppliedSignature.Length != 32)
            return false;
        var bytes = Encoding.UTF8.GetBytes(payload);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), suppliedDigest) ||
            !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(options.Key, bytes), suppliedSignature))
            return false;
        try { evidence = JsonSerializer.Deserialize<FarmJobEvidencePayload>(payload, CanonicalOptions); }
        catch (JsonException) { return false; }
        return evidence?.Version == Version;
    }

    public static byte[] Export(
        FarmJobEvidencePayload evidence,
        string keyId,
        string digest,
        string signature) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new FarmJobEvidenceEnvelope(Algorithm, keyId, digest, signature, evidence),
            ExportOptions);

    private static bool TryHex(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { bytes = Convert.FromHexString(value); return true; }
        catch (FormatException) { return false; }
    }

    private static bool SamePostgresTimestamp(DateTimeOffset left, DateTimeOffset? right) =>
        right is not null && Math.Abs((left - right.Value).Ticks) < 10;

    private static bool SameNullablePostgresTimestamp(
        DateTimeOffset? left,
        DateTimeOffset? right) =>
        left is null && right is null ||
        left is not null && SamePostgresTimestamp(left.Value, right);

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions ExportOptions = new(CanonicalOptions)
    {
        WriteIndented = true
    };
}
