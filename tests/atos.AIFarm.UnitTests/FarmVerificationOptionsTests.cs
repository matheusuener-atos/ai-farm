using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class FarmVerificationOptionsTests
{
    [Fact]
    public void DefaultIsDisabledSoGenerationStillPaysNothing()
    {
        var options = FarmVerificationOptions.Disabled;

        Assert.False(options.Enabled);
        Assert.Equal(options, options.Validate());
    }

    [Fact]
    public void ValidateAcceptsCoherentEnabledKnobs()
    {
        var options = new FarmVerificationOptions(
            Enabled: true,
            CpPerSecondMilli: 6_000,
            MinGenerationCpMilli: 3_000,
            MaxGenerationCpMilli: 100_000,
            RequiredVerifiedFreshnessSeconds: 900);

        Assert.Equal(options, options.Validate());
    }

    [Fact]
    public void CeilingBelowFloorIsRejected()
    {
        var options = new FarmVerificationOptions(
            MinGenerationCpMilli: 100_000,
            MaxGenerationCpMilli: 3_000);

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void CpPerSecondMilliOutOfRangeIsRejected(long cpPerSecondMilli)
    {
        var options = FarmVerificationOptions.Disabled with { CpPerSecondMilli = cpPerSecondMilli };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Theory]
    [InlineData(29)]
    [InlineData(86_401)]
    public void FreshnessOutOfRangeIsRejected(int freshnessSeconds)
    {
        var options = FarmVerificationOptions.Disabled with
        {
            RequiredVerifiedFreshnessSeconds = freshnessSeconds
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }
}
