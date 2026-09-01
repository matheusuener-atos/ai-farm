using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using atos.AIFarm.Agent;
using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class AgentClientTests
{
    [Fact]
    public void RemoteManifestRequiresValidNodeSignatureAndAllowlist()
    {
        var nodeId = Guid.NewGuid();
        var configuration = new AgentConfiguration(
            "https://farm.example.com",
            nodeId,
            "credential-target",
            AgentInventory.Version,
            30);
        var key = RandomNumberGenerator.GetBytes(32);
        var offer = SignedOffer(nodeId, key);

        var manifest = RemoteJobVerifier.Verify(
            offer,
            configuration,
            Convert.ToBase64String(key),
            DateTimeOffset.UtcNow);

        Assert.Equal(offer.JobId, manifest.JobId);
        Assert.Throws<InvalidOperationException>(() => RemoteJobVerifier.Verify(
            offer with { ManifestPayload = offer.ManifestPayload + " " },
            configuration,
            Convert.ToBase64String(key),
            DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => RemoteJobVerifier.Verify(
            offer,
            configuration,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => RemoteJobVerifier.Verify(
            offer,
            configuration with
            {
                Policy = configuration.EffectivePolicy with { AcceptJobs = false }
            },
            Convert.ToBase64String(key),
            DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => RemoteJobVerifier.Verify(
            SignedOffer(nodeId, key, new string('0', 64)),
            configuration,
            Convert.ToBase64String(key),
            DateTimeOffset.UtcNow));
    }

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
        var activeJobId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        HttpRequestMessage? captured = null;
        string? body = null;
        using var handler = new StubHandler(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """
                {"nodeId":"11111111-1111-1111-1111-111111111111","status":"Online","acceptJobs":true,"serverTimeUtc":"2026-09-01T12:00:00Z","heartbeatIntervalSeconds":30,"activeHeartbeatIntervalSeconds":2,"activeJobId":"22222222-2222-2222-2222-222222222222","leaseExpiresAtUtc":"2026-09-01T12:00:10Z","cancelActiveJob":true}
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

        var result = await client.HeartbeatAsync(
            configuration,
            "secret-value",
            CancellationToken.None,
            activeJobId);

        Assert.Equal(FarmNodeStatus.Online, result.Status);
        Assert.Equal("Bearer", captured!.Headers.Authorization!.Scheme);
        Assert.Equal("secret-value", captured.Headers.Authorization.Parameter);
        Assert.Contains("\"agentVersion\":\"atos-agent/0.4.0\"", body);
        Assert.Contains("\"acceptJobs\":true", body);
        Assert.Contains("\"maxLogicalProcessors\":1", body);
        Assert.Contains("\"maxMemoryBytes\":268435456", body);
        Assert.Contains($"\"activeJobId\":\"{activeJobId}\"", body);
        Assert.Equal(activeJobId, result.ActiveJobId);
        Assert.Equal(2, result.ActiveHeartbeatIntervalSeconds);
        Assert.True(result.CancelActiveJob);
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
                30,
                Policy: new AgentLocalPolicy(true, 1, 128L * 1024 * 1024));

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

    private static FarmRemoteJobOffer SignedOffer(
        Guid nodeId,
        byte[] key,
        string? nodeProfileHash = null)
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = new RemoteJobManifest(
            "atos-farm-manifest-v3-agent-profile",
            Guid.NewGuid(),
            Guid.NewGuid(),
            nodeId,
            "SHA256_CHAIN",
            "atos-sha256-chain-v1",
            "DETERMINISTIC_REEXECUTION_V1",
            Guid.NewGuid().ToString("N"),
            100_000,
            30,
            1,
            1,
            "DENY_ALL",
            "STANDARD",
            now,
            now.AddMinutes(5),
            nodeProfileHash ?? FarmNodeProfileFingerprint.Compute(
                AgentInventory.Heartbeat().Runtime,
                AgentInventory.Heartbeat().LogicalProcessors,
                AgentInventory.Heartbeat().MemoryBytes,
                AgentInventory.Version));
        var payload = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        var bytes = Encoding.UTF8.GetBytes(payload);
        return new FarmRemoteJobOffer(
            manifest.JobId,
            manifest.AttemptId,
            payload,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Convert.ToHexString(HMACSHA256.HashData(key, bytes)).ToLowerInvariant(),
            "HMAC-SHA256-NODE-V1");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => response(request);
    }
}
