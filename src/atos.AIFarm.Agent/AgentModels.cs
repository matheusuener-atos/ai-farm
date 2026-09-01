using System.Runtime.InteropServices;
using atos.AIFarm.Core.Application;

namespace atos.AIFarm.Agent;

internal sealed record AgentConfiguration(
    string BaseUrl,
    Guid NodeId,
    string CredentialTarget,
    string AgentVersion,
    int HeartbeatIntervalSeconds);

internal static class AgentInventory
{
    public const string Version = "atos-agent/0.1.0";

    public static FarmAgentEnrollmentRequest Enrollment() => new(
        Environment.MachineName.ToUpperInvariant(),
        Runtime(),
        Math.Max(1, Environment.ProcessorCount),
        Memory(),
        Version);

    public static FarmAgentHeartbeatRequest Heartbeat() => new(
        Runtime(),
        Math.Max(1, Environment.ProcessorCount),
        Memory(),
        Version);

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
