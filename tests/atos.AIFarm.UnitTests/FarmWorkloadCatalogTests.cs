using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class FarmWorkloadCatalogTests
{
    [Fact]
    public void CatalogHasUniqueImmutableIdentifiersAndSafePolicies()
    {
        Assert.Equal(6, FarmWorkloadCatalog.Definitions.Count);
        Assert.Equal(
            FarmWorkloadCatalog.Definitions.Count,
            FarmWorkloadCatalog.Definitions.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(FarmWorkloadCatalog.Definitions, definition =>
        {
            Assert.Equal("STANDARD", definition.DataClassification);
            Assert.True(definition.ProcessingPointsMilli > 0);
            if (definition.Id == FarmWorkloadCatalog.GenerateId)
            {
                // ADR 0043: geração usa o runtime local do node e não é
                // reexecutável — a exceção deliberada e única do catálogo.
                Assert.Equal(FarmWorkloadCatalog.UnverifiedGenerationPolicy, definition.VerificationPolicy);
                Assert.Equal(FarmWorkloadCatalog.LocalhostModelNetworkPolicy, definition.NetworkPolicy);
                Assert.InRange(definition.TimeoutSeconds, 30, 120);
            }
            else
            {
                Assert.Equal("DENY_ALL", definition.NetworkPolicy);
                Assert.StartsWith("DETERMINISTIC_", definition.VerificationPolicy, StringComparison.Ordinal);
                Assert.Equal(1, definition.MaxLogicalProcessors);
                Assert.InRange(definition.MaxMemoryBytes, 1, 256L * 1024 * 1024);
            }
        });
    }

    [Fact]
    public void MerkleRootIsDeterministicAndInputSensitive()
    {
        var definition = FarmWorkloadCatalog.GetRequired("merkle-root-sha256-v1");
        var first = FarmDeterministicWorkloads.Compute(
            definition.Workload, "00112233445566778899aabbccddeeff", definition.Iterations);
        var repeated = FarmDeterministicWorkloads.Compute(
            definition.Workload, "00112233445566778899aabbccddeeff", definition.Iterations);
        var changed = FarmDeterministicWorkloads.Compute(
            definition.Workload, "10112233445566778899aabbccddeeff", definition.Iterations);

        Assert.Equal(64, first.Length);
        Assert.Equal(first, repeated);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void CatalogRejectsUnknownWorkload()
    {
        Assert.Throws<ArgumentException>(() => FarmWorkloadCatalog.GetRequired("shell-v1"));
        Assert.Throws<ArgumentException>(() => FarmDeterministicWorkloads.Compute(
            "ARBITRARY_COMMAND", "nonce", 1));
    }
}
