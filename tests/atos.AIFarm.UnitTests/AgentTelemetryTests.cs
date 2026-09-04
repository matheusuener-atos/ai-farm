using atos.AIFarm.Agent;
using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

// AgentTelemetry é singleton do processo do Agent; os testes compartilham a
// instância e por isso vivem na mesma classe (execução sequencial no xUnit),
// cada um começando por ResetForTests.
public sealed class AgentTelemetryTests
{
    [Fact]
    public void JobLifecycleAccumulatesSessionCounters()
    {
        var telemetry = AgentTelemetry.Instance;
        telemetry.ResetForTests();
        var verified = Guid.NewGuid();
        var failed = Guid.NewGuid();

        telemetry.JobStarted(verified, "atos-sha256-chain-v1", 1, 256L * 1024 * 1024);
        telemetry.JobStarted(failed, "atos-merkle-root-sha256-v1", 1, 128L * 1024 * 1024);
        Assert.Equal(2, telemetry.Snapshot().ActiveJobs.Count);

        telemetry.JobFinished(verified, true, 1_000, 480, 96L * 1024 * 1024);
        telemetry.JobFinished(failed, false, 0, 120, 32L * 1024 * 1024);

        var snapshot = telemetry.Snapshot();
        Assert.Empty(snapshot.ActiveJobs);
        Assert.Equal(1, snapshot.JobsCompleted);
        Assert.Equal(1, snapshot.JobsFailed);
        Assert.Equal(1_000, snapshot.VerifiedCpMilli);
        Assert.Equal(600, snapshot.WorkerCpuMilliseconds);
        Assert.Equal(96L * 1024 * 1024, snapshot.PeakWorkerMemoryBytes);
    }

    [Fact]
    public void LogKeepsOnlyTheMostRecentEntries()
    {
        var telemetry = AgentTelemetry.Instance;
        telemetry.ResetForTests();
        for (var index = 0; index < 200; index++)
            telemetry.Log("TESTE", $"mensagem {index}");

        var log = telemetry.Snapshot().Log;
        Assert.Equal(150, log.Count);
        Assert.Equal("mensagem 50", log[0].Message);
        Assert.Equal("mensagem 199", log[^1].Message);
    }

    [Fact]
    public void PolicyAndIdentityFlowIntoTheSnapshot()
    {
        var telemetry = AgentTelemetry.Instance;
        telemetry.ResetForTests();
        var nodeId = Guid.NewGuid();
        telemetry.SetBinding(nodeId, "https://farm.example");
        telemetry.SetPolicy(new AgentLocalPolicy(false, 1, 128L * 1024 * 1024));
        telemetry.SetPhase("PAUSADO", "pelo painel");
        telemetry.SetIdentity(new FarmAgentIdentity(
            nodeId, "MEU-PC", "conta@exemplo", true, "Online", "Verified",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var snapshot = telemetry.Snapshot();
        Assert.Equal(nodeId, snapshot.NodeId);
        Assert.Equal("https://farm.example", snapshot.BaseUrl);
        Assert.False(snapshot.AcceptJobs);
        Assert.Equal("PAUSADO", snapshot.Phase);
        Assert.Equal("pelo painel", snapshot.PhaseDetail);
        Assert.Equal("conta@exemplo", snapshot.Identity?.AccountDisplayName);
        Assert.True(snapshot.Identity?.SharePlatformWork);
    }

    [Fact]
    public void CredentialHandleExposesTheLatestValueToRunningReaders()
    {
        var handle = new AgentCredentialHandle { Current = "antiga" };
        Assert.Equal("antiga", handle.Current);
        handle.Current = "rotacionada";
        Assert.Equal("rotacionada", handle.Current);
    }
}
