using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class FarmManifestSigningOptions
{
    private const int MinimumKeyBytes = 32;
    private readonly byte[] key;

    public FarmManifestSigningOptions(string base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
            throw new ArgumentException("A chave de assinatura do Compute Lab é obrigatória.", nameof(base64Key));

        try { key = Convert.FromBase64String(base64Key); }
        catch (FormatException exception)
        {
            throw new ArgumentException("A chave de assinatura do Compute Lab deve estar em Base64.", nameof(base64Key), exception);
        }

        if (key.Length < MinimumKeyBytes)
            throw new ArgumentException("A chave de assinatura do Compute Lab deve ter pelo menos 256 bits.", nameof(base64Key));
    }

    internal ReadOnlySpan<byte> Key => key;

    internal FarmAgentManifestKey DeriveForAgent(Guid nodeId)
    {
        var derived = HMACSHA256.HashData(
            Key,
            Encoding.UTF8.GetBytes($"atos-agent-manifest-v1:{nodeId:N}"));
        var verificationKey = Convert.ToBase64String(derived);
        var keyId = Convert.ToHexString(SHA256.HashData(derived))
            .ToLowerInvariant()[..16];
        return new FarmAgentManifestKey(
            new FarmManifestSigningOptions(verificationKey),
            verificationKey,
            keyId);
    }

    public static FarmManifestSigningOptions Development { get; } = new(
        Convert.ToBase64String(SHA256.HashData(
            Encoding.UTF8.GetBytes("atos-ai-farm-development-manifest-key-v1"))));
}

internal sealed record FarmAgentManifestKey(
    FarmManifestSigningOptions Signing,
    string VerificationKey,
    string KeyId);

internal sealed record FarmJobManifest(
    string Version,
    Guid JobId,
    Guid AttemptId,
    Guid NodeId,
    string Workload,
    string WorkloadVersion,
    string VerificationPolicy,
    string Nonce,
    int Iterations,
    int TimeoutSeconds,
    int MaxLogicalProcessors,
    long MaxMemoryBytes,
    string NetworkPolicy,
    string DataClassification,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? NodeProfileHash = null,
    string? InputDigest = null);

internal sealed record SignedFarmJobManifest(
    FarmJobManifest Manifest,
    string Payload,
    string Digest,
    string Signature);

internal static class FarmManifestSecurity
{
    internal const string EmbeddedVersion = "atos-farm-manifest-v1";
    internal const string LegacyAgentVersion = "atos-farm-manifest-v2-agent";
    internal const string AgentProfileVersion = "atos-farm-manifest-v3-agent-profile";

    public static SignedFarmJobManifest Sign(
        FarmJobManifest manifest,
        FarmManifestSigningOptions options)
    {
        var payload = JsonSerializer.Serialize(manifest, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var signature = Convert.ToHexString(HMACSHA256.HashData(options.Key, bytes)).ToLowerInvariant();
        return new SignedFarmJobManifest(manifest, payload, digest, signature);
    }

    public static bool TryVerify(
        string? payload,
        string? expectedDigest,
        string? expectedSignature,
        FarmManifestSigningOptions options,
        out FarmJobManifest? manifest)
    {
        manifest = null;
        if (string.IsNullOrWhiteSpace(payload) ||
            !TryHex(expectedDigest, out var suppliedDigest) ||
            !TryHex(expectedSignature, out var suppliedSignature))
            return false;

        var bytes = Encoding.UTF8.GetBytes(payload);
        var digest = SHA256.HashData(bytes);
        var signature = HMACSHA256.HashData(options.Key, bytes);
        if (!CryptographicOperations.FixedTimeEquals(digest, suppliedDigest) ||
            !CryptographicOperations.FixedTimeEquals(signature, suppliedSignature))
            return false;

        try { manifest = JsonSerializer.Deserialize<FarmJobManifest>(payload, JsonOptions); }
        catch (JsonException) { return false; }
        return manifest is not null;
    }

    public static bool TryVerifyJobBinding(
        FarmJobRecord job,
        FarmManifestSigningOptions rootOptions,
        out FarmJobManifest? manifest)
    {
        var verification = job.ManifestVersion is AgentProfileVersion or LegacyAgentVersion
            ? rootOptions.DeriveForAgent(job.NodeId).Signing
            : rootOptions;
        if (!TryVerify(
                job.ManifestPayload,
                job.ManifestDigest,
                job.ManifestSignature,
                verification,
                out manifest) ||
            manifest is null)
            return false;

        return manifest.Version is EmbeddedVersion or AgentProfileVersion or LegacyAgentVersion &&
            manifest.Version == job.ManifestVersion &&
            manifest.JobId == job.Id &&
            manifest.AttemptId == job.AttemptId &&
            manifest.NodeId == job.NodeId &&
            manifest.Workload == job.Workload &&
            manifest.WorkloadVersion == job.WorkloadVersion &&
            manifest.VerificationPolicy == job.VerificationPolicy &&
            manifest.Nonce == job.Nonce &&
            manifest.InputDigest == job.InputDigest &&
            SamePostgresTimestamp(manifest.IssuedAtUtc, job.ManifestIssuedAtUtc) &&
            SamePostgresTimestamp(manifest.ExpiresAtUtc, job.ManifestExpiresAtUtc);
    }

    private static bool TryHex(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { bytes = Convert.FromHexString(value); return true; }
        catch (FormatException) { return false; }
    }

    private static bool SamePostgresTimestamp(DateTimeOffset left, DateTimeOffset? right) =>
        right is not null && Math.Abs((left - right.Value).Ticks) < 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };
}
