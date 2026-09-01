using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using atos.AIFarm.Core.Application;

namespace atos.AIFarm.Agent;

internal sealed record AgentLocalPolicy(
    bool AcceptJobs,
    int MaxLogicalProcessors,
    long MaxMemoryBytes)
{
    public static AgentLocalPolicy Default() => new(
        true,
        Math.Min(1, AgentInventory.LogicalProcessors),
        Math.Min(256L * 1024 * 1024, AgentInventory.MemoryBytes));

    public static AgentLocalPolicy Normalize(AgentLocalPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaxLogicalProcessors < 1 ||
            policy.MaxLogicalProcessors > AgentInventory.LogicalProcessors)
            throw new ArgumentOutOfRangeException(
                nameof(policy.MaxLogicalProcessors),
                $"CPU autorizada deve estar entre 1 e {AgentInventory.LogicalProcessors}.");
        if (policy.MaxMemoryBytes < 1 ||
            policy.MaxMemoryBytes > AgentInventory.MemoryBytes)
            throw new ArgumentOutOfRangeException(
                nameof(policy.MaxMemoryBytes),
                "Memória autorizada deve ser positiva e não pode exceder a memória detectada.");
        return policy;
    }
}

internal sealed record AgentConfiguration(
    string BaseUrl,
    Guid NodeId,
    string CredentialTarget,
    string AgentVersion,
    int HeartbeatIntervalSeconds,
    string? ManifestKeyTarget = null,
    string? ManifestKeyId = null,
    AgentLocalPolicy? Policy = null)
{
    public AgentLocalPolicy EffectivePolicy =>
        AgentLocalPolicy.Normalize(Policy ?? AgentLocalPolicy.Default());
}

internal sealed record RemoteJobManifest(
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

internal static class RemoteJobVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static RemoteJobManifest Verify(
        FarmRemoteJobOffer offer,
        AgentConfiguration configuration,
        string verificationKey,
        DateTimeOffset now)
    {
        if (offer.SignatureAlgorithm != "HMAC-SHA256-NODE-V1")
            throw new InvalidOperationException("Algoritmo de assinatura remoto não autorizado.");
        var payload = Encoding.UTF8.GetBytes(offer.ManifestPayload);
        var digest = DecodeHex(offer.ManifestDigest, "digest");
        var signature = DecodeHex(offer.ManifestSignature, "assinatura");
        byte[] key;
        try { key = Convert.FromBase64String(verificationKey); }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Chave de verificação local inválida.", exception);
        }
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), digest) ||
                !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, payload), signature))
                throw new InvalidOperationException("O manifesto remoto não possui assinatura válida para este Node.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var manifest = JsonSerializer.Deserialize<RemoteJobManifest>(offer.ManifestPayload, JsonOptions)
            ?? throw new InvalidOperationException("Manifesto remoto vazio.");
        var local = AgentInventory.Heartbeat(configuration.EffectivePolicy);
        var localProfileHash = FarmNodeProfileFingerprint.Compute(
            local.Runtime,
            local.LogicalProcessors,
            local.MemoryBytes,
            local.AgentVersion);
        if (manifest.Version != "atos-farm-manifest-v3-agent-profile" ||
            manifest.JobId != offer.JobId ||
            manifest.AttemptId != offer.AttemptId ||
            manifest.NodeId != configuration.NodeId ||
            manifest.Workload != "SHA256_CHAIN" ||
            manifest.WorkloadVersion != "atos-sha256-chain-v1" ||
            manifest.VerificationPolicy != "DETERMINISTIC_REEXECUTION_V1" ||
            manifest.NetworkPolicy != "DENY_ALL" ||
            manifest.DataClassification != "STANDARD" ||
            manifest.Iterations != 100_000 ||
            manifest.TimeoutSeconds != 30 ||
            manifest.MaxLogicalProcessors is < 1 or > 1 ||
            !local.AcceptJobs ||
            manifest.MaxLogicalProcessors > local.MaxLogicalProcessors ||
            manifest.MaxMemoryBytes is <= 0 or > 256L * 1024 * 1024 ||
            manifest.MaxMemoryBytes > local.MaxMemoryBytes ||
            !string.Equals(manifest.NodeProfileHash, localProfileHash, StringComparison.Ordinal) ||
            manifest.ExpiresAtUtc <= now)
            throw new InvalidOperationException("O manifesto remoto viola a allowlist ou os limites locais.");
        return manifest;
    }

    private static byte[] DecodeHex(string value, string field)
    {
        try
        {
            var bytes = Convert.FromHexString(value);
            if (bytes.Length != 32) throw new FormatException();
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"O {field} do manifesto é inválido.", exception);
        }
    }
}

internal static class AgentDeterministicBenchmark
{
    public static string Compute(string nonce, int iterations, CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
        for (var index = 0; index < iterations; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            hash = SHA256.HashData(hash);
        }
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal static class AgentInventory
{
    public const string Version = FarmAgentProtocol.CurrentAgentVersion;
    public static int LogicalProcessors => Math.Max(1, Environment.ProcessorCount);
    public static long MemoryBytes => Memory();

    public static FarmAgentEnrollmentRequest Enrollment() => new(
        Environment.MachineName.ToUpperInvariant(),
        Runtime(),
        LogicalProcessors,
        MemoryBytes,
        Version);

    public static FarmAgentHeartbeatRequest Heartbeat(AgentLocalPolicy? policy = null)
    {
        var effective = AgentLocalPolicy.Normalize(policy ?? AgentLocalPolicy.Default());
        return new(
        Runtime(),
        LogicalProcessors,
        MemoryBytes,
        Version,
        effective.AcceptJobs,
        effective.MaxLogicalProcessors,
        effective.MaxMemoryBytes);
    }

    private static string Runtime() =>
        $"{RuntimeInformation.FrameworkDescription} // {RuntimeInformation.OSDescription}";

    private static long Memory() =>
        Math.Max(1, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
}

internal static class AgentEndpoint
{
    public static string Normalize(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Informe uma URL HTTP(S) absoluta, sem query ou fragmento.", nameof(value));

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            throw new ArgumentException("HTTP sem TLS só é permitido para localhost.", nameof(value));

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}

internal sealed class AgentAuthenticationException(string message) : Exception(message);
