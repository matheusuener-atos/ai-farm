using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace atos.AIFarm.Core.Application;

public sealed record FarmWorkloadDefinition(
    string Id,
    string Workload,
    string Version,
    string DisplayName,
    string Description,
    string VerificationPolicy,
    int Iterations,
    int TimeoutSeconds,
    int MaxLogicalProcessors,
    long MaxMemoryBytes,
    long ProcessingPointsMilli,
    string NetworkPolicy,
    string DataClassification);

public static class FarmWorkloadCatalog
{
    public const string Version = "atos-workload-catalog-v1";
    public const string DefaultId = "sha256-chain-v1";

    public static IReadOnlyList<FarmWorkloadDefinition> Definitions { get; } =
    [
        new(
            DefaultId,
            "SHA256_CHAIN",
            "atos-sha256-chain-v1",
            "SHA-256 CHAIN",
            "Benchmark encadeado para medir e verificar processamento determinístico.",
            "DETERMINISTIC_REEXECUTION_V1",
            100_000,
            30,
            1,
            256L * 1024 * 1024,
            1_000,
            "DENY_ALL",
            "STANDARD"),
        new(
            "merkle-root-sha256-v1",
            "MERKLE_ROOT_SHA256",
            "atos-merkle-root-sha256-v1",
            "MERKLE ROOT SHA-256",
            "Constrói e verifica uma árvore de integridade determinística com 4.096 folhas.",
            "DETERMINISTIC_REEXECUTION_V1",
            4_096,
            30,
            1,
            256L * 1024 * 1024,
            1_000,
            "DENY_ALL",
            "STANDARD")
    ];

    public static FarmWorkloadDefinition GetRequired(string id) =>
        Definitions.SingleOrDefault(x => string.Equals(x.Id, id?.Trim(), StringComparison.Ordinal))
        ?? throw new ArgumentException("O workload não pertence ao catálogo autorizado.", nameof(id));

    public static FarmWorkloadDefinition? Find(string workload, string version) =>
        Definitions.SingleOrDefault(x =>
            string.Equals(x.Workload, workload, StringComparison.Ordinal) &&
            string.Equals(x.Version, version, StringComparison.Ordinal));
}

public static class FarmDeterministicWorkloads
{
    public static string Compute(
        string workload,
        string nonce,
        int iterations,
        CancellationToken cancellationToken = default) => workload switch
        {
            "SHA256_CHAIN" => ComputeSha256Chain(nonce, iterations, cancellationToken),
            "MERKLE_ROOT_SHA256" => ComputeMerkleRoot(nonce, iterations, cancellationToken),
            _ => throw new ArgumentException("Workload não autorizado.", nameof(workload))
        };

    private static string ComputeSha256Chain(
        string nonce,
        int iterations,
        CancellationToken cancellationToken)
    {
        Validate(nonce, iterations);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
        for (var index = 0; index < iterations; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            hash = SHA256.HashData(hash);
        }
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeMerkleRoot(
        string nonce,
        int leafCount,
        CancellationToken cancellationToken)
    {
        Validate(nonce, leafCount);
        if (!BitOperations.IsPow2(leafCount) || leafCount > 65_536)
            throw new ArgumentOutOfRangeException(nameof(leafCount), "A árvore exige uma quantidade de folhas em potência de dois.");

        var seed = Encoding.UTF8.GetBytes(nonce);
        var leaves = new byte[leafCount][];
        for (var index = 0; index < leafCount; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var payload = new byte[seed.Length + sizeof(int)];
            seed.CopyTo(payload, 0);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(seed.Length), index);
            leaves[index] = SHA256.HashData(payload);
        }

        var level = leaves;
        while (level.Length > 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = new byte[level.Length / 2][];
            for (var index = 0; index < level.Length; index += 2)
            {
                var pair = new byte[64];
                level[index].CopyTo(pair, 0);
                level[index + 1].CopyTo(pair, 32);
                parent[index / 2] = SHA256.HashData(pair);
            }
            level = parent;
        }
        return Convert.ToHexString(level[0]).ToLowerInvariant();
    }

    private static void Validate(string nonce, int iterations)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            throw new ArgumentException("Nonce obrigatório.", nameof(nonce));
        if (iterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(iterations));
    }
}
