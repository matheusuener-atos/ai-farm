using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class FarmAnalyticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuildsAccountMetricsDailySeriesAndPeriodComparison()
    {
        var node = new FarmAnalyticsNode(
            Guid.NewGuid(), "node-01", FarmNodeStatus.Online, FarmVerificationLevel.Verified,
            FarmNodeConnectionKind.Agent, true, true, 8, 16L << 30, Now.AddSeconds(-10), "atos-agent/1");
        var economics = new FarmEconomicsPipelineAnalytics(2, 3, 2_500, 1.25m);
        var jobs = new[]
        {
            Job(FarmJobStatus.Completed, 1_000, 3_600_000, Now.AddHours(-2), "sha256"),
            Job(FarmJobStatus.TimedOut, 0, 1_000, Now.AddHours(-1), "sha256"),
            Job(FarmJobStatus.Running, 0, null, Now.AddMinutes(-10), "inference"),
            Job(FarmJobStatus.Completed, 9_999_000, 9_999_000, Now.AddMinutes(-5), "sha256", false),
            Job(FarmJobStatus.Completed, 500, 500, Now.AddDays(-8), "sha256")
        };

        var analytics = FarmAnalyticsBuilder.Build(jobs, node, economics, 7, Now);

        Assert.Equal(4, analytics.JobCount);
        Assert.Equal(1, analytics.ActiveJobCount);
        Assert.Equal(1, analytics.CompletedJobCount);
        Assert.Equal(1, analytics.FailedJobCount);
        Assert.Equal(1, analytics.IntegrityFailureCount);
        Assert.Equal(33.3m, analytics.SuccessRatePercent);
        Assert.Equal(1_000, analytics.VerifiedProcessingPointsMilli);
        Assert.Equal(1m, analytics.ProcessingPointsPerHour);
        Assert.Equal(1_800_500m, analytics.AverageDurationMilliseconds);
        Assert.Equal(256L << 20, analytics.PeakMemoryBytes);
        Assert.Equal(3_601_000, analytics.ProcessorTimeMilliseconds);
        Assert.Equal(500, analytics.PreviousPeriodProcessingPointsMilli);
        Assert.Equal(100m, analytics.ProcessingPointsChangePercent);
        Assert.Equal(7, analytics.Daily.Count);
        Assert.Equal(4, analytics.Daily.Sum(x => x.JobCount));
        Assert.Equal(2, analytics.Workloads.Count);
        Assert.Equal(4, analytics.Statuses.Sum(x => x.JobCount));
        Assert.Equal(node, analytics.Node);
        Assert.Equal(economics, analytics.Economics);
    }

    [Fact]
    public void EmptyPeriodReturnsContinuousZeroSeries()
    {
        var analytics = FarmAnalyticsBuilder.Build(
            [], null, new FarmEconomicsPipelineAnalytics(0, 0, 0, 0), 30, Now);

        Assert.Equal(30, analytics.Daily.Count);
        Assert.All(analytics.Daily, day => Assert.Equal(0, day.JobCount));
        Assert.Equal(0, analytics.PeakMemoryBytes);
        Assert.Null(analytics.AverageDurationMilliseconds);
        Assert.Null(analytics.ProcessingPointsChangePercent);
        Assert.Empty(analytics.Workloads);
        Assert.Empty(analytics.Statuses);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void RejectsPeriodsOutsideSupportedRange(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FarmAnalyticsBuilder.Build(
            [], null, new FarmEconomicsPipelineAnalytics(0, 0, 0, 0), days, Now));
    }

    private static FarmAnalyticsJob Job(
        FarmJobStatus status,
        long points,
        long? duration,
        DateTimeOffset createdAt,
        string workload,
        bool evidenceValid = true) => new(
            Guid.NewGuid(), workload, "v1", status, points, duration,
            256L << 20, duration, "TEST", createdAt,
            duration is null ? null : createdAt.AddMilliseconds(duration.Value),
            evidenceValid);
}
