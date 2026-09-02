using System.Text.Json;
using atos.AIFarm.Agent;
using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class AgentWorkerProcessTests
{
    [Fact]
    public async Task WorkerHostAcceptsOnlyAllowlistedRequest()
    {
        var request = new AgentWorkerRequest(
            AgentWorkerProtocol.Version,
            AgentWorkerProtocol.Workload,
            "atos-sha256-chain-v1",
            "00112233445566778899aabbccddeeff",
            AgentWorkerProtocol.Iterations);
        using var input = new StringReader(JsonSerializer.Serialize(
            request,
            AgentWorkerProtocol.JsonOptions));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await AgentWorkerHost.RunAsync(input, output, error);

        var result = JsonSerializer.Deserialize<AgentWorkerResult>(
            output.ToString(),
            AgentWorkerProtocol.JsonOptions);
        Assert.Equal(0, exitCode);
        Assert.NotNull(result);
        Assert.Equal(AgentWorkerProtocol.Version, result.Protocol);
        Assert.Equal(64, result.Digest.Length);
        Assert.True(result.DurationMilliseconds > 0);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task WorkerHostRejectsWorkOutsideAllowlist()
    {
        var request = new AgentWorkerRequest(
            AgentWorkerProtocol.Version,
            "ARBITRARY_COMMAND",
            "arbitrary-v1",
            "00112233445566778899aabbccddeeff",
            AgentWorkerProtocol.Iterations);
        using var input = new StringReader(JsonSerializer.Serialize(
            request,
            AgentWorkerProtocol.JsonOptions));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await AgentWorkerHost.RunAsync(input, output, error);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("allowlist", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkerHostExecutesMerkleRootFromCatalog()
    {
        var definition = FarmWorkloadCatalog.GetRequired("merkle-root-sha256-v1");
        var request = new AgentWorkerRequest(
            AgentWorkerProtocol.Version,
            definition.Workload,
            definition.Version,
            "00112233445566778899aabbccddeeff",
            definition.Iterations);
        using var input = new StringReader(JsonSerializer.Serialize(request, AgentWorkerProtocol.JsonOptions));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await AgentWorkerHost.RunAsync(input, output, error);
        var result = JsonSerializer.Deserialize<AgentWorkerResult>(output.ToString(), AgentWorkerProtocol.JsonOptions);

        Assert.Equal(0, exitCode);
        Assert.NotNull(result);
        Assert.Equal(
            FarmDeterministicWorkloads.Compute(
                definition.Workload, request.Nonce, definition.Iterations),
            result.Digest);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task WorkerHostExecutesUsefulTextAndReturnsVerifiableOutput()
    {
        const string text = "Iris prepara contexto verificável para a Atos.";
        var definition = FarmWorkloadCatalog.GetRequired("iris-text-index-v1");
        var request = new AgentWorkerRequest(
            AgentWorkerProtocol.Version, definition.Workload, definition.Version,
            "00112233445566778899aabbccddeeff", definition.Iterations, text);
        using var input = new StringReader(JsonSerializer.Serialize(request, AgentWorkerProtocol.JsonOptions));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await AgentWorkerHost.RunAsync(input, output, error);
        var result = JsonSerializer.Deserialize<AgentWorkerResult>(output.ToString(), AgentWorkerProtocol.JsonOptions);

        Assert.Equal(0, exitCode);
        Assert.NotNull(result);
        Assert.NotNull(result.OutputPayload);
        Assert.Equal(FarmDeterministicWorkloads.Execute(
            definition.Workload, request.Nonce, definition.Iterations, text).Digest, result.Digest);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ProcessRunnerExecutesDedicatedChildAndValidatesResult()
    {
        var runner = CreateRunner();
        var manifest = Manifest();

        var result = await runner.RunAsync(manifest, CancellationToken.None);

        Assert.Equal(
            AgentDeterministicBenchmark.Compute(
                manifest.Nonce,
                manifest.Iterations,
                CancellationToken.None),
            result.Digest);
        Assert.True(result.DurationMilliseconds > 0);
        Assert.True(result.PeakMemoryBytes > 0);
        Assert.True(result.ProcessorTimeMilliseconds > 0);
        Assert.True(FarmWorkerEnforcement.IsSupported(result.Enforcement));
        if (OperatingSystem.IsWindows())
            Assert.StartsWith("WINDOWS_JOB_OBJECT", result.Enforcement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessRunnerKillsChildWhenCancelled()
    {
        var runner = CreateRunner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(Manifest(), cancellation.Token));
    }

    private static AgentWorkerProcessRunner CreateRunner()
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        return new AgentWorkerProcessRunner(new AgentWorkerLaunch(
            dotnet,
            [typeof(AgentApplication).Assembly.Location]));
    }

    private static RemoteJobManifest Manifest() => new(
        "atos-farm-manifest-v3-agent-profile",
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        AgentWorkerProtocol.Workload,
        "atos-sha256-chain-v1",
        "DETERMINISTIC_REEXECUTION_V1",
        "00112233445566778899aabbccddeeff",
        AgentWorkerProtocol.Iterations,
        30,
        1,
        256L * 1024 * 1024,
        "DENY_ALL",
        "STANDARD",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(5),
        new string('a', 64));
}
