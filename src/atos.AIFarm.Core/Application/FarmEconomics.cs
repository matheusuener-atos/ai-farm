namespace atos.AIFarm.Core.Application;

public sealed record FarmEconomicsScenario(
    decimal MonthlyAvailableHours,
    decimal PowerWatts,
    decimal ElectricityCostPerKwh,
    decimal ComputeUnitsPerHour,
    decimal UtilizationPercent,
    decimal SuccessPercent,
    decimal GrossValuePerThousandCu,
    decimal VerificationCostPercent,
    decimal PlatformCostPercent,
    decimal TaxesAndFeesPercent,
    decimal MarginPercent,
    decimal RiskReservePercent);

public sealed record FarmEconomicsResult(
    decimal EffectiveHours,
    decimal AttemptedComputeUnits,
    decimal VerifiedComputeUnits,
    decimal EnergyKwh,
    decimal GrossEconomicValue,
    decimal FailureLoss,
    decimal EnergyCost,
    decimal VerificationCost,
    decimal PlatformCost,
    decimal TaxesAndFees,
    decimal RequiredMargin,
    decimal RiskReserve,
    decimal MaximumFundedReward,
    decimal MaximumRewardPerThousandCu,
    decimal FundingGap,
    decimal TotalNonRewardCosts,
    decimal? BreakEvenGrossValuePerThousandCu)
{
    public bool IsEconomicallyViable => MaximumFundedReward > 0 && FundingGap == 0;
}

public static class FarmEconomicsCalculator
{
    public static FarmEconomicsResult Calculate(FarmEconomicsScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        Positive(scenario.MonthlyAvailableHours, nameof(scenario.MonthlyAvailableHours));
        Positive(scenario.PowerWatts, nameof(scenario.PowerWatts));
        NonNegative(scenario.ElectricityCostPerKwh, nameof(scenario.ElectricityCostPerKwh));
        Positive(scenario.ComputeUnitsPerHour, nameof(scenario.ComputeUnitsPerHour));
        Percentage(scenario.UtilizationPercent, nameof(scenario.UtilizationPercent));
        Percentage(scenario.SuccessPercent, nameof(scenario.SuccessPercent));
        NonNegative(scenario.GrossValuePerThousandCu, nameof(scenario.GrossValuePerThousandCu));
        Percentage(scenario.VerificationCostPercent, nameof(scenario.VerificationCostPercent));
        Percentage(scenario.PlatformCostPercent, nameof(scenario.PlatformCostPercent));
        Percentage(scenario.TaxesAndFeesPercent, nameof(scenario.TaxesAndFeesPercent));
        Percentage(scenario.MarginPercent, nameof(scenario.MarginPercent));
        Percentage(scenario.RiskReservePercent, nameof(scenario.RiskReservePercent));

        var effectiveHours = scenario.MonthlyAvailableHours * Rate(scenario.UtilizationPercent);
        var attemptedCu = effectiveHours * scenario.ComputeUnitsPerHour;
        var verifiedCu = attemptedCu * Rate(scenario.SuccessPercent);
        var grossPotentialValue = ValueOf(attemptedCu, scenario.GrossValuePerThousandCu);
        var grossEconomicValue = ValueOf(verifiedCu, scenario.GrossValuePerThousandCu);
        var failureLoss = grossPotentialValue - grossEconomicValue;
        var energyKwh = effectiveHours * scenario.PowerWatts / 1_000m;
        var energyCost = energyKwh * scenario.ElectricityCostPerKwh;
        var verificationCost = grossEconomicValue * Rate(scenario.VerificationCostPercent);
        var platformCost = grossEconomicValue * Rate(scenario.PlatformCostPercent);
        var taxesAndFees = grossEconomicValue * Rate(scenario.TaxesAndFeesPercent);
        var requiredMargin = grossEconomicValue * Rate(scenario.MarginPercent);
        var riskReserve = grossEconomicValue * Rate(scenario.RiskReservePercent);
        var deductions = energyCost + verificationCost + platformCost + taxesAndFees + requiredMargin + riskReserve;
        var availableForReward = grossEconomicValue - deductions;
        var maximumReward = decimal.Max(0, availableForReward);
        var fundingGap = decimal.Max(0, -availableForReward);
        var rewardPerThousandCu = verifiedCu == 0 ? 0 : maximumReward * 1_000m / verifiedCu;
        var percentageDeductions = Rate(
            scenario.VerificationCostPercent +
            scenario.PlatformCostPercent +
            scenario.TaxesAndFeesPercent +
            scenario.MarginPercent +
            scenario.RiskReservePercent);
        decimal? breakEvenValue = verifiedCu == 0 || percentageDeductions >= 1
            ? null
            : energyCost * 1_000m / (verifiedCu * (1 - percentageDeductions));

        return new FarmEconomicsResult(
            effectiveHours, attemptedCu, verifiedCu, energyKwh, grossEconomicValue,
            failureLoss, energyCost, verificationCost, platformCost, taxesAndFees,
            requiredMargin, riskReserve, maximumReward, rewardPerThousandCu, fundingGap,
            deductions, breakEvenValue);
    }

    public static IReadOnlyList<FarmEconomicsProjection> Analyze(FarmEconomicsScenario baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var stress = baseline with
        {
            ElectricityCostPerKwh = baseline.ElectricityCostPerKwh * 1.20m,
            GrossValuePerThousandCu = baseline.GrossValuePerThousandCu * 0.80m,
            UtilizationPercent = decimal.Min(100m, baseline.UtilizationPercent * 0.75m),
            SuccessPercent = decimal.Max(0m, baseline.SuccessPercent - 10m)
        };
        var favorable = baseline with
        {
            ElectricityCostPerKwh = baseline.ElectricityCostPerKwh * 0.90m,
            GrossValuePerThousandCu = baseline.GrossValuePerThousandCu * 1.10m,
            UtilizationPercent = decimal.Min(100m, baseline.UtilizationPercent * 1.15m),
            SuccessPercent = decimal.Min(100m, baseline.SuccessPercent + 3m)
        };

        return
        [
            new(FarmEconomicsProjectionKind.Stress, stress, Calculate(stress)),
            new(FarmEconomicsProjectionKind.Baseline, baseline, Calculate(baseline)),
            new(FarmEconomicsProjectionKind.Favorable, favorable, Calculate(favorable))
        ];
    }

    private static decimal Rate(decimal percent) => percent / 100m;
    private static decimal ValueOf(decimal cu, decimal valuePerThousandCu) => cu * valuePerThousandCu / 1_000m;

    private static void Positive(decimal value, string name)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(name, "O valor deve ser maior que zero.");
    }

    private static void NonNegative(decimal value, string name)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(name, "O valor não pode ser negativo.");
    }

    private static void Percentage(decimal value, string name)
    {
        if (value is < 0 or > 100)
            throw new ArgumentOutOfRangeException(name, "O percentual deve estar entre 0 e 100.");
    }
}

public enum FarmEconomicsProjectionKind { Stress, Baseline, Favorable }

public sealed record FarmEconomicsProjection(
    FarmEconomicsProjectionKind Kind,
    FarmEconomicsScenario Scenario,
    FarmEconomicsResult Result);
