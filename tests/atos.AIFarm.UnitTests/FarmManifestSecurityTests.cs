using atos.AIFarm.Infrastructure.Persistence;

namespace atos.AIFarm.UnitTests;

public sealed class FarmManifestSecurityTests
{
    private static readonly FarmManifestSigningOptions Signing = new(
        Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()));

    [Fact]
    public void SignedCanonicalManifestVerifiesAndRoundTrips()
    {
        var manifest = CreateManifest();

        var signed = FarmManifestSecurity.Sign(manifest, Signing);
        var verified = FarmManifestSecurity.TryVerify(
            signed.Payload,
            signed.Digest,
            signed.Signature,
            Signing,
            out var restored);

        Assert.True(verified);
        Assert.Equal(manifest, restored);
        Assert.Equal(64, signed.Digest.Length);
        Assert.Equal(64, signed.Signature.Length);
    }

    [Fact]
    public void PayloadOrKeyTamperingInvalidatesSignature()
    {
        var signed = FarmManifestSecurity.Sign(CreateManifest(), Signing);
        var otherKey = new FarmManifestSigningOptions(
            Convert.ToBase64String(Enumerable.Repeat((byte)99, 32).ToArray()));

        Assert.False(FarmManifestSecurity.TryVerify(
            signed.Payload.Replace("SHA256_CHAIN", "UNSAFE_WORKLOAD", StringComparison.Ordinal),
            signed.Digest,
            signed.Signature,
            Signing,
            out _));
        Assert.False(FarmManifestSecurity.TryVerify(
            signed.Payload,
            signed.Digest,
            signed.Signature,
            otherKey,
            out _));
    }

    [Fact]
    public void SigningKeyRequiresAtLeast256Bits()
    {
        var shortKey = Convert.ToBase64String(new byte[16]);

        Assert.Throws<ArgumentException>(() => new FarmManifestSigningOptions(shortKey));
        Assert.Throws<ArgumentException>(() => new FarmManifestSigningOptions("not-base64"));
    }

    [Fact]
    public void CompletedJobEvidenceIsBoundToJobAndDetectsTampering()
    {
        var now = DateTimeOffset.Parse("2026-09-02T01:00:00Z");
        var jobId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var attemptId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var nodeId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var key = Signing.DeriveForAgent(nodeId);
        var manifest = new FarmJobManifest(
            FarmManifestSecurity.AgentProfileVersion,
            jobId,
            attemptId,
            nodeId,
            "SHA256_CHAIN",
            "atos-sha256-chain-v1",
            "DETERMINISTIC_REEXECUTION_V1",
            "00112233445566778899aabbccddeeff",
            100_000,
            30,
            1,
            256L * 1024 * 1024,
            "DENY_ALL",
            "STANDARD",
            now.AddMinutes(-1),
            now.AddMinutes(4),
            new string('f', 64));
        var signedManifest = FarmManifestSecurity.Sign(manifest, key.Signing);
        var job = new FarmJobRecord
        {
            Id = jobId,
            AttemptId = attemptId,
            NodeId = nodeId,
            ReservedLogicalProcessors = manifest.MaxLogicalProcessors,
            ReservedMemoryBytes = manifest.MaxMemoryBytes,
            Workload = "SHA256_CHAIN",
            WorkloadVersion = "atos-sha256-chain-v1",
            VerificationPolicy = "DETERMINISTIC_REEXECUTION_V1",
            Status = "Completed",
            Nonce = manifest.Nonce,
            ManifestVersion = manifest.Version,
            ManifestDigest = signedManifest.Digest,
            ManifestSignature = signedManifest.Signature,
            ManifestPayload = signedManifest.Payload,
            ManifestIssuedAtUtc = manifest.IssuedAtUtc,
            ManifestExpiresAtUtc = manifest.ExpiresAtUtc,
            ResultDigest = new string('c', 64),
            ExpectedDigest = new string('c', 64),
            ComputeUnitsMilli = 1_000,
            DurationMilliseconds = 25,
            WorkerPeakMemoryBytes = 32 * 1024 * 1024,
            WorkerProcessorTimeMilliseconds = 8,
            WorkerEnforcement = "PROCESS_MONITOR+CPU_AFFINITY",
            CreatedAtUtc = now.AddSeconds(-2),
            StartedAtUtc = now.AddSeconds(-1),
            FinishedAtUtc = now
        };
        FarmJobEventRecord[] events =
        [
            new() { JobId = jobId, Sequence = 1, Status = "Queued", Detail = "Criado.", OccurredAtUtc = now.AddSeconds(-2) },
            new() { JobId = jobId, Sequence = 2, Status = "Completed", Detail = "Verificado.", OccurredAtUtc = now }
        ];
        var signed = FarmEvidenceSecurity.Sign(job, events, key.Signing, key.KeyId);
        job.EvidenceVersion = FarmEvidenceSecurity.Version;
        job.EvidencePayload = signed.Payload;
        job.EvidenceDigest = signed.Digest;
        job.EvidenceSignature = signed.Signature;
        job.EvidenceKeyId = key.KeyId;
        job.EvidenceCreatedAtUtc = now;

        var verification = FarmEvidenceSecurity.Verify(job, events, Signing, out var restored);
        Assert.True(verification.IsValid);
        Assert.True(verification.SignatureValid);
        Assert.True(verification.ManifestValid);
        Assert.True(verification.JobBindingValid);
        Assert.True(verification.LifecycleValid);
        Assert.Equal(job.Id, restored?.JobId);
        Assert.Equal(2, restored?.Events.Count);

        job.ReservedMemoryBytes /= 2;
        verification = FarmEvidenceSecurity.Verify(job, events, Signing, out _);
        Assert.False(verification.IsValid);
        Assert.False(verification.ManifestValid);
        job.ReservedMemoryBytes = manifest.MaxMemoryBytes;

        job.ResultDigest = new string('d', 64);
        verification = FarmEvidenceSecurity.Verify(job, events, Signing, out _);
        Assert.False(verification.IsValid);
        Assert.True(verification.SignatureValid);
        Assert.False(verification.JobBindingValid);

        job.ResultDigest = new string('c', 64);
        events[0].Detail = "Adulterado.";
        verification = FarmEvidenceSecurity.Verify(job, events, Signing, out _);
        Assert.False(verification.IsValid);
        Assert.True(verification.SignatureValid);
        Assert.True(verification.JobBindingValid);
        Assert.False(verification.LifecycleValid);
    }

    [Fact]
    public void RotatedRootKeyStillValidatesHistoricalEvidenceViaRetiredKeys()
    {
        var oldKeyBase64 = Convert.ToBase64String(
            Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        var newKeyBase64 = Convert.ToBase64String(
            Enumerable.Repeat((byte)7, 32).ToArray());
        var oldRoot = new FarmManifestSigningOptions(oldKeyBase64);
        var rotatedRoot = new FarmManifestSigningOptions(newKeyBase64, [oldKeyBase64]);
        var rotatedWithoutRetired = new FarmManifestSigningOptions(newKeyBase64);

        var (job, events) = CreateSealedAgentJob(oldRoot);

        var withRing = FarmEvidenceSecurity.Verify(job, events, rotatedRoot, out var restored);
        Assert.True(withRing.IsValid);
        Assert.True(withRing.SignatureValid);
        Assert.True(withRing.ManifestValid);
        Assert.True(withRing.JobBindingValid);
        Assert.True(withRing.LifecycleValid);
        Assert.Equal(job.Id, restored?.JobId);

        var withoutRing = FarmEvidenceSecurity.Verify(job, events, rotatedWithoutRetired, out _);
        Assert.False(withoutRing.SignatureValid);
        Assert.False(withoutRing.ManifestValid);
        Assert.False(withoutRing.IsValid);

        Assert.NotEqual(
            oldRoot.DeriveForAgent(job.NodeId).KeyId,
            rotatedRoot.DeriveForAgent(job.NodeId).KeyId);
    }

    private static (FarmJobRecord Job, FarmJobEventRecord[] Events) CreateSealedAgentJob(
        FarmManifestSigningOptions root)
    {
        var now = DateTimeOffset.Parse("2026-09-03T01:00:00Z");
        var jobId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var attemptId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var nodeId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var key = root.DeriveForAgent(nodeId);
        var manifest = new FarmJobManifest(
            FarmManifestSecurity.AgentProfileVersion,
            jobId,
            attemptId,
            nodeId,
            "SHA256_CHAIN",
            "atos-sha256-chain-v1",
            "DETERMINISTIC_REEXECUTION_V1",
            "00112233445566778899aabbccddeeff",
            100_000,
            30,
            1,
            256L * 1024 * 1024,
            "DENY_ALL",
            "STANDARD",
            now.AddMinutes(-1),
            now.AddMinutes(4),
            new string('f', 64));
        var signedManifest = FarmManifestSecurity.Sign(manifest, key.Signing);
        var job = new FarmJobRecord
        {
            Id = jobId,
            AttemptId = attemptId,
            NodeId = nodeId,
            ReservedLogicalProcessors = manifest.MaxLogicalProcessors,
            ReservedMemoryBytes = manifest.MaxMemoryBytes,
            Workload = "SHA256_CHAIN",
            WorkloadVersion = "atos-sha256-chain-v1",
            VerificationPolicy = "DETERMINISTIC_REEXECUTION_V1",
            Status = "Completed",
            Nonce = manifest.Nonce,
            ManifestVersion = manifest.Version,
            ManifestDigest = signedManifest.Digest,
            ManifestSignature = signedManifest.Signature,
            ManifestPayload = signedManifest.Payload,
            ManifestIssuedAtUtc = manifest.IssuedAtUtc,
            ManifestExpiresAtUtc = manifest.ExpiresAtUtc,
            ResultDigest = new string('c', 64),
            ExpectedDigest = new string('c', 64),
            ComputeUnitsMilli = 1_000,
            DurationMilliseconds = 25,
            WorkerPeakMemoryBytes = 32 * 1024 * 1024,
            WorkerProcessorTimeMilliseconds = 8,
            WorkerEnforcement = "PROCESS_MONITOR+CPU_AFFINITY",
            CreatedAtUtc = now.AddSeconds(-2),
            StartedAtUtc = now.AddSeconds(-1),
            FinishedAtUtc = now
        };
        FarmJobEventRecord[] events =
        [
            new() { JobId = jobId, Sequence = 1, Status = "Queued", Detail = "Criado.", OccurredAtUtc = now.AddSeconds(-2) },
            new() { JobId = jobId, Sequence = 2, Status = "Completed", Detail = "Verificado.", OccurredAtUtc = now }
        ];
        var sealedEvidence = FarmEvidenceSecurity.Sign(job, events, key.Signing, key.KeyId);
        job.EvidenceVersion = FarmEvidenceSecurity.Version;
        job.EvidencePayload = sealedEvidence.Payload;
        job.EvidenceDigest = sealedEvidence.Digest;
        job.EvidenceSignature = sealedEvidence.Signature;
        job.EvidenceKeyId = key.KeyId;
        job.EvidenceCreatedAtUtc = now;
        return (job, events);
    }

    private static FarmJobManifest CreateManifest() => new(
        "atos-farm-manifest-v1",
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        "SHA256_CHAIN",
        "atos-sha256-chain-v1",
        "DETERMINISTIC_REEXECUTION_V1",
        "00112233445566778899aabbccddeeff",
        100_000,
        30,
        1,
        256L * 1024 * 1024,
        "DENY_ALL",
        "STANDARD",
        DateTimeOffset.Parse("2026-09-01T10:00:00Z"),
        DateTimeOffset.Parse("2026-09-01T10:05:00Z"));
}
