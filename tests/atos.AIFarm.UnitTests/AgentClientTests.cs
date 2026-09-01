using System.Net;
using System.Text;
using atos.AIFarm.Agent;
using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class AgentClientTests
{
    [Theory]
    [InlineData("http://localhost:5159/", "http://localhost:5159")]
    [InlineData("https://farm.example.com/path", "https://farm.example.com")]
    public void EndpointNormalizesSafeAddresses(string value, string expected) =>
        Assert.Equal(expected, AgentEndpoint.Normalize(value));

    [Theory]
    [InlineData("http://farm.example.com")]
    [InlineData("https://farm.example.com?secret=value")]
    [InlineData("not-a-url")]
    public void EndpointRejectsUnsafeAddresses(string value) =>
        Assert.Throws<ArgumentException>(() => AgentEndpoint.Normalize(value));

    [Fact]
    public async Task HeartbeatUsesBearerCredentialAndReadsTextualStatus()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        using var handler = new StubHandler(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """
                {"nodeId":"11111111-1111-1111-1111-111111111111","status":"Online","acceptJobs":true,"serverTimeUtc":"2026-09-01T12:00:00Z","heartbeatIntervalSeconds":30}
                """);
        });
        using var http = new HttpClient(handler);
        var client = new AgentClient(http);
        var configuration = new AgentConfiguration(
            "https://farm.example.com",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "test-target",
            AgentInventory.Version,
            30);

        var result = await client.HeartbeatAsync(configuration, "secret-value", CancellationToken.None);

        Assert.Equal(FarmNodeStatus.Online, result.Status);
        Assert.Equal("Bearer", captured!.Headers.Authorization!.Scheme);
        Assert.Equal("secret-value", captured.Headers.Authorization.Parameter);
        Assert.Contains("\"agentVersion\":\"atos-agent/0.1.0\"", body);
        Assert.Equal("https://farm.example.com/api/farm/agents/heartbeat", captured.RequestUri!.ToString());
    }

    [Fact]
    public async Task UnauthorizedHeartbeatRaisesAuthenticationError()
    {
        using var handler = new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var http = new HttpClient(handler);
        var client = new AgentClient(http);
        var configuration = new AgentConfiguration(
            "https://farm.example.com",
            Guid.NewGuid(),
            "test-target",
            AgentInventory.Version,
            30);

        await Assert.ThrowsAsync<AgentAuthenticationException>(() =>
            client.HeartbeatAsync(configuration, "invalid", CancellationToken.None));
    }

    [Fact]
    public async Task ConfigurationStoreRoundTripsWithoutCredential()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"atos-agent-test-{Guid.NewGuid():N}");
        try
        {
            var store = new AgentConfigurationStore(directory);
            var expected = new AgentConfiguration(
                "https://farm.example.com",
                Guid.NewGuid(),
                "credential-target",
                AgentInventory.Version,
                30);

            await store.SaveAsync(expected);
            var restored = await store.LoadAsync();

            Assert.Equal(expected, restored);
            Assert.DoesNotContain("secret", await File.ReadAllTextAsync(store.ConfigurationPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => response(request);
    }
}
