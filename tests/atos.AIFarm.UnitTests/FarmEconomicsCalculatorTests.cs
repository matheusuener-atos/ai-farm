using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class FarmEconomicsCalculatorTests
{
    [Fact]
    public void Calculate_ProducesAuditableMaximumFundedReward()
    {
        var result = FarmEconomicsCalculator.Calculate(Scenario());

        Assert.Equal(35m, result.EffectiveHours);
        Assert.Equal(35_000m, result.AttemptedComputeUnits);
        Assert.Equal(33_250m, result.VerifiedComputeUnits);
        Assert.Equal(4.2m, result.EnergyKwh);
        Assert.Equal(16.625m, result.GrossEconomicValue);
        Assert.Equal(0.875m, result.FailureLoss);
        Assert.Equal(3.99m, result.EnergyCost);
        Assert.Equal(4.655m, result.MaximumFundedReward);
        Assert.Equal(11.97m, result.TotalNonRewardCosts);
        Assert.Equal(0.2307692307692307692307692308m, result.BreakEvenGrossValuePerThousandCu);
        Assert.Equal(0m, result.FundingGap);
        Assert.True(result.IsEconomicallyViable);
    }

    [Fact]
    public void Analyze_ProducesOrderedStressBaselineAndFavorableProjections()
    {
        var projections = FarmEconomicsCalculator.Analyze(Scenario());

        Assert.Collection(
            projections,
            stress => Assert.Equal(FarmEconomicsProjectionKind.Stress, stress.Kind),
            baseline => Assert.Equal(FarmEconomicsProjectionKind.Baseline, baseline.Kind),
            favorable => Assert.Equal(FarmEconomicsProjectionKind.Favorable, favorable.Kind));
        Assert.True(projections[0].Result.MaximumFundedReward < projections[1].Result.MaximumFundedReward);
        Assert.True(projections[1].Result.MaximumFundedReward < projections[2].Result.MaximumFundedReward);
    }

    [Fact]
    public void Calculate_HasNoBreakEvenWhenPercentageDeductionsConsumeAllRevenue()
    {
        var result = FarmEconomicsCalculator.Calculate(Scenario() with
        {
            VerificationCostPercent = 100m
        });

        Assert.Null(result.BreakEvenGrossValuePerThousandCu);
    }

    [Fact]
    public void Calculate_NeverProducesNegativeReward()
    {
        var result = FarmEconomicsCalculator.Calculate(Scenario() with
        {
            ElectricityCostPerKwh = 10m
        });

        Assert.Equal(0m, result.MaximumFundedReward);
        Assert.True(result.FundingGap > 0);
        Assert.False(result.IsEconomicallyViable);
    }

    [Fact]
    public void Calculate_DoesNotConvertRewardToAic()
    {
        var result = FarmEconomicsCalculator.Calculate(Scenario());

        Assert.DoesNotContain(
            result.GetType().GetProperties(),
            property => property.Name.Contains("Aic", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Calculate_RejectsInvalidPercentages(int utilization)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FarmEconomicsCalculator.Calculate(Scenario() with
            {
                UtilizationPercent = utilization
            }));
    }

    private static FarmEconomicsScenario Scenario() => new(
        MonthlyAvailableHours: 100m,
        PowerWatts: 120m,
        ElectricityCostPerKwh: 0.95m,
        ComputeUnitsPerHour: 1_000m,
        UtilizationPercent: 35m,
        SuccessPercent: 95m,
        GrossValuePerThousandCu: 0.50m,
        VerificationCostPercent: 10m,
        PlatformCostPercent: 10m,
        TaxesAndFeesPercent: 8m,
        MarginPercent: 15m,
        RiskReservePercent: 5m);
}
