using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class FarmRewardPolicyTests
{
    [Fact]
    public void EnabledPolicyCalculatesIntegerRewardAndAppliesCap()
    {
        var policy = new FarmRewardPolicy(true, "policy-v1", 2_000, 100_000, 5_000).Validate();

        Assert.Equal(2, policy.CalculateRewardMinor(1_000));
        Assert.Equal(5_000, policy.CalculateRewardMinor(5_000_000));
    }

    [Fact]
    public void CalculationRoundsDownAndRejectsInvalidInputs()
    {
        var policy = new FarmRewardPolicy(true, "policy-v1", 1, 100, 100).Validate();

        Assert.Equal(0, policy.CalculateRewardMinor(999_999));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.CalculateRewardMinor(0));
    }

    [Theory]
    [InlineData(0, 100, 100)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 100, 0)]
    [InlineData(1, 100, 101)]
    public void EnabledPolicyRequiresCoherentPositiveLimits(long rate, long budget, long cap)
    {
        var policy = new FarmRewardPolicy(true, "policy-v1", rate, budget, cap);

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("policy with spaces")]
    [InlineData("política-v1")]
    public void PolicyVersionMustBeStableAsciiIdentifier(string version)
    {
        var policy = new FarmRewardPolicy(false, version, 0, 0, 0);

        Assert.Throws<ArgumentException>(() => policy.Validate());
    }

    [Fact]
    public void DisabledPolicyCanCarryZeroLimitsButCannotCalculate()
    {
        var policy = FarmRewardPolicy.Disabled.Validate();

        Assert.False(policy.IsEnabled);
        Assert.Throws<InvalidOperationException>(() => policy.CalculateRewardMinor(1_000));
    }
}
