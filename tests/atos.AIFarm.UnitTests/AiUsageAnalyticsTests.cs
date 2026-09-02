using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;

namespace atos.AIFarm.UnitTests;

public sealed class AiUsageAnalyticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuilderCreatesContinuousSeriesBreakdownsAndPeriodComparison()
    {
        var currentSuccess = Execution(
            "Completed", Now.AddDays(-1), Aic.FromAic(20), Aic.FromAic(12), 100, 50, 2_000);
        var currentFailure = Execution(
            "Timeout", Now.AddHours(-2), Aic.FromAic(2), 0, 0, 0, 1_000);
        var previousSuccess = Execution(
            "Completed", Now.AddDays(-10), Aic.FromAic(10), Aic.FromAic(6), 40, 20, 500);

        var analytics = AiUsageAnalyticsBuilder.Build(
            [currentSuccess, currentFailure, previousSuccess],
            7,
            Now);

        Assert.Equal(7, analytics.Daily.Count);
        Assert.Equal(2, analytics.Totals.ExecutionCount);
        Assert.Equal(Aic.FromAic(12), analytics.Totals.ActualCostMinor);
        Assert.Equal(Aic.FromAic(10), analytics.Totals.ReleasedCostMinor);
        Assert.Equal(50m, analytics.SuccessRatePercent);
        Assert.Equal(1_500m, analytics.AverageDurationMilliseconds);
        Assert.Equal(Aic.FromAic(6), analytics.PreviousPeriodActualCostMinor);
        Assert.Equal(100m, analytics.ActualCostChangePercent);
        Assert.Equal(2, Assert.Single(analytics.Routes).ExecutionCount);
        Assert.Collection(
            analytics.Statuses,
            first => Assert.Equal(1, first.ExecutionCount),
            second => Assert.Equal(1, second.ExecutionCount));
        var activeDay = analytics.Daily.Single(x => x.DateUtc == DateOnly.FromDateTime(Now.AddDays(-1).Date));
        Assert.Equal(Aic.FromAic(12), activeDay.ActualCostMinor);
    }

    [Fact]
    public void BuilderReturnsZeroedPeriodWithoutExecutions()
    {
        var analytics = AiUsageAnalyticsBuilder.Build([], 30, Now);

        Assert.Equal(30, analytics.Daily.Count);
        Assert.Equal(0, analytics.Totals.ExecutionCount);
        Assert.Equal(0, analytics.SuccessRatePercent);
        Assert.Null(analytics.AverageDurationMilliseconds);
        Assert.Null(analytics.ActualCostChangePercent);
        Assert.Empty(analytics.Routes);
        Assert.Empty(analytics.Statuses);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void BuilderRejectsPeriodsOutsideContract(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AiUsageAnalyticsBuilder.Build([], days, Now));
    }

    private static AiExecutionSummary Execution(
        string status,
        DateTimeOffset startedAt,
        long estimated,
        long actual,
        long input,
        long output,
        int durationMilliseconds) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Teste analítico",
            "OpenAI",
            "test-model",
            "catalog-v1",
            input,
            output,
            estimated,
            actual,
            status,
            startedAt,
            startedAt.AddMilliseconds(durationMilliseconds));
}
