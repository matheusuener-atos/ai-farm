using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using atos.AIFarm.Core.Application;

namespace atos.AIFarm.Agent;

internal sealed class AgentClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<FarmAgentEnrollmentResult> EnrollAsync(
        string baseUrl,
        string enrollmentToken,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{AgentEndpoint.Normalize(baseUrl)}/api/farm/agents/enroll")
        {
            Content = JsonContent.Create(AgentInventory.Enrollment(), options: JsonOptions)
        };
        message.Headers.Add("X-Enrollment-Token", enrollmentToken);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new AgentAuthenticationException("Código de vínculo inválido, expirado ou já utilizado.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FarmAgentEnrollmentResult>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("O servidor retornou uma inscrição vazia.");
    }

    public async Task<FarmAgentHeartbeatResult> HeartbeatAsync(
        AgentConfiguration configuration,
        string credential,
        CancellationToken cancellationToken,
        Guid? activeJobId = null)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{AgentEndpoint.Normalize(configuration.BaseUrl)}/api/farm/agents/heartbeat")
        {
            Content = JsonContent.Create(
                AgentInventory.Heartbeat(configuration.EffectivePolicy, activeJobId),
                options: JsonOptions)
        };
        message.Headers.Authorization = new("Bearer", credential);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new AgentAuthenticationException("A credencial do Agent foi recusada pelo servidor.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FarmAgentHeartbeatResult>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("O servidor retornou um heartbeat vazio.");
    }

    public async Task<FarmAgentTrust> GetTrustAsync(
        AgentConfiguration configuration,
        string credential,
        CancellationToken cancellationToken)
    {
        using var message = Authorized(
            HttpMethod.Get,
            $"{AgentEndpoint.Normalize(configuration.BaseUrl)}/api/farm/agents/trust",
            credential);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        EnsureAuthorized(response);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FarmAgentTrust>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("O servidor retornou trust vazio.");
    }

    public async Task<FarmRemoteJobOffer?> ClaimJobAsync(
        AgentConfiguration configuration,
        string credential,
        CancellationToken cancellationToken)
    {
        using var message = Authorized(
            HttpMethod.Post,
            $"{AgentEndpoint.Normalize(configuration.BaseUrl)}/api/farm/agents/jobs/claim",
            credential);
        message.Content = JsonContent.Create(new { }, options: JsonOptions);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        EnsureAuthorized(response);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FarmRemoteJobOffer>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("O servidor retornou uma oferta vazia.");
    }

    public async Task<bool> MarkJobStartedAsync(
        AgentConfiguration configuration,
        string credential,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        using var message = Authorized(
            HttpMethod.Post,
            $"{AgentEndpoint.Normalize(configuration.BaseUrl)}/api/farm/agents/jobs/{jobId}/started",
            credential);
        message.Content = JsonContent.Create(new { }, options: JsonOptions);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        EnsureAuthorized(response);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task RejectJobAsync(
        AgentConfiguration configuration,
        string credential,
        Guid jobId,
        string reason,
        CancellationToken cancellationToken,
        FarmRemoteJobRejectionKind kind = FarmRemoteJobRejectionKind.Policy)
    {
        using var message = Authorized(
            HttpMethod.Post,
            $"{AgentEndpoint.Normalize(configuration.BaseUrl)}/api/farm/agents/jobs/{jobId}/reject",
            credential);
        message.Content = JsonContent.Create(new FarmRemoteJobRejection(reason, kind), options: JsonOptions);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        EnsureAuthorized(response);
        response.EnsureSuccessStatusCode();
    }

    public async Task<FarmRemoteJobAcknowledgement> SubmitResultAsync(
        AgentConfiguration configuration,
        string credential,
        Guid jobId,
        FarmRemoteJobResult result,
        CancellationToken cancellationToken)
    {
        using var message = Authorized(
            HttpMethod.Post,
            $"{AgentEndpoint.Normalize(configuration.BaseUrl)}/api/farm/agents/jobs/{jobId}/result",
            credential);
        message.Content = JsonContent.Create(result, options: JsonOptions);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        EnsureAuthorized(response);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FarmRemoteJobAcknowledgement>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("O servidor retornou confirmação vazia.");
    }

    private static HttpRequestMessage Authorized(
        HttpMethod method,
        string url,
        string credential)
    {
        var message = new HttpRequestMessage(method, url);
        message.Headers.Authorization = new("Bearer", credential);
        return message;
    }

    private static void EnsureAuthorized(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new AgentAuthenticationException("A credencial do Agent foi recusada pelo servidor.");
    }
}
