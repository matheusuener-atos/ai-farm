using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class FarmCapacityTests
{
    [Fact]
    public void CatalogResourceContractsAreInternallyConsistent()
    {
        Assert.All(FarmWorkloadCatalog.Definitions, definition =>
        {
            Assert.Same(definition, definition.ValidateResources());
            Assert.InRange(definition.RequiredLogicalProcessors, 1, definition.MaxLogicalProcessors);
            Assert.InRange(definition.RequiredMemoryBytes, 1, definition.MaxMemoryBytes);
        });
    }

    [Fact]
    public void EvaluatorExplainsPolicyResourceAndOccupancyBlockers()
    {
        var definition = FarmWorkloadCatalog.GetRequired("iris-text-index-v1");
        var node = Node(memory: definition.RequiredMemoryBytes);
        Assert.Equal(FarmCapacityBlocker.None,
            FarmCapacityEvaluator.Evaluate(node, definition, false, true));
        Assert.Equal(FarmCapacityBlocker.Busy,
            FarmCapacityEvaluator.Evaluate(node, definition, true, true));
        Assert.Equal(FarmCapacityBlocker.InsufficientMemory,
            FarmCapacityEvaluator.Evaluate(Node(memory: definition.RequiredMemoryBytes - 1), definition, false, true));
        Assert.Equal(FarmCapacityBlocker.StaleHeartbeat,
            FarmCapacityEvaluator.Evaluate(node with { IsHeartbeatFresh = false, Status = FarmNodeStatus.Offline }, definition, false, true));
        Assert.Equal(FarmCapacityBlocker.EmbeddedSuppressed,
            FarmCapacityEvaluator.Evaluate(node with { ConnectionKind = FarmNodeConnectionKind.Embedded }, definition, false, true));
    }

    private static FarmNodeSnapshot Node(long memory) => new(
        Guid.NewGuid(), "AGENT", FarmNodeStatus.Online, FarmVerificationLevel.Detected,
        ".NET 10", 4, 1024L * 1024 * 1024, DateTimeOffset.UtcNow,
        FarmNodeConnectionKind.Agent, FarmAgentProtocol.CurrentAgentVersion,
        true, true, 4, memory, null, null);
}
