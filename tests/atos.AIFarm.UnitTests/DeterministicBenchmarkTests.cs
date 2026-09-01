using atos.AIFarm.Infrastructure.Persistence;

namespace atos.AIFarm.UnitTests;

public sealed class DeterministicBenchmarkTests
{
    [Fact]
    public void SameManifestProducesSameDigest()
    {
        var first = DeterministicBenchmark.Compute("nonce-controlado", 100);
        var second = DeterministicBenchmark.Compute("nonce-controlado", 100);

        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void NonceAndWorkAmountArePartOfTheProof()
    {
        var baseline = DeterministicBenchmark.Compute("nonce-a", 100);

        Assert.NotEqual(baseline, DeterministicBenchmark.Compute("nonce-b", 100));
        Assert.NotEqual(baseline, DeterministicBenchmark.Compute("nonce-a", 101));
    }
}
