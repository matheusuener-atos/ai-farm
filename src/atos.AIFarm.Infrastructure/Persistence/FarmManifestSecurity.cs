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
    string? NodeProfileHash = null);

internal sealed record SignedFarmJobManifest(
    FarmJobManifest Manifest,
    string Payload,
    string Digest,
    string Signature);

internal static class FarmManifestSecurity
{
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

    private static bool TryHex(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { bytes = Convert.FromHexString(value); return true; }
        catch (FormatException) { return false; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };
}
