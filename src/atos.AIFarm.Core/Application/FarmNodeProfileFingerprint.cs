using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace atos.AIFarm.Core.Application;

public static class FarmNodeProfileFingerprint
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static string Compute(
        string runtime,
        int logicalProcessors,
        long memoryBytes,
        string? agentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
        if (logicalProcessors < 1) throw new ArgumentOutOfRangeException(nameof(logicalProcessors));
        if (memoryBytes < 1) throw new ArgumentOutOfRangeException(nameof(memoryBytes));

        var canonical = JsonSerializer.Serialize(
            new Profile(
                "atos-node-profile-v1",
                runtime,
                logicalProcessors,
                memoryBytes,
                agentVersion),
            JsonOptions);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed record Profile(
        string Version,
        string Runtime,
        int LogicalProcessors,
        long MemoryBytes,
        string? AgentVersion);
}
