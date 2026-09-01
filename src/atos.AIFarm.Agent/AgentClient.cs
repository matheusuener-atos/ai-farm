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
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{AgentEndpoint.Normalize(configuration.BaseUrl)}/api/farm/agents/heartbeat")
        {
            Content = JsonContent.Create(AgentInventory.Heartbeat(), options: JsonOptions)
        };
        message.Headers.Authorization = new("Bearer", credential);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new AgentAuthenticationException("A credencial do Agent foi recusada pelo servidor.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FarmAgentHeartbeatResult>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("O servidor retornou um heartbeat vazio.");
    }
}
