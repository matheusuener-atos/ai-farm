using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class FarmNodeReliabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewNodeStartsUnprovenWithNeutralPriorScore()
    {
        var result = FarmNodeReliabilityEvaluator.Evaluate([], Now);

        Assert.Equal(80, result.Score);
        Assert.Equal(FarmNodeReliabilityBand.Unproven, result.Band);
        Assert.False(result.IsQuarantined);
    }

    [Fact]
    public void SuccessesIncreaseTrustAndCancellationIsNeutral()
    {
        var result = FarmNodeReliabilityEvaluator.Evaluate(
        [
            Outcome(FarmJobStatus.Cancelled, 1),
            Outcome(FarmJobStatus.Completed, 2),
            Outcome(FarmJobStatus.Completed, 3),
            Outcome(FarmJobStatus.Completed, 4),
            Outcome(FarmJobStatus.Completed, 5),
            Outcome(FarmJobStatus.Completed, 6)
        ], Now);

        Assert.Equal(5, result.SampleSize);
        Assert.Equal(90, result.Score);
        Assert.Equal(FarmNodeReliabilityBand.Trusted, result.Band);
    }

    [Theory]
    [InlineData(FarmJobStatus.Failed)]
    [InlineData(FarmJobStatus.TimedOut)]
    [InlineData(FarmJobStatus.Rejected)]
    public void ThreeRecentFailuresOpenQuarantine(FarmJobStatus status)
    {
        var result = FarmNodeReliabilityEvaluator.Evaluate(
        [
            Outcome(status, 1),
            Outcome(status, 2),
            Outcome(status, 3),
            Outcome(FarmJobStatus.Completed, 60)
        ], Now);

        Assert.True(result.IsQuarantined);
        Assert.Equal(3, result.ConsecutiveFailures);
        Assert.Equal(Now.AddMinutes(29), result.QuarantinedUntilUtc);
    }

    [Fact]
    public void QuarantineExpiresAndNextSuccessRestoresConsecutiveFailures()
    {
        var failed = new[]
        {
            Outcome(FarmJobStatus.Failed, 40),
            Outcome(FarmJobStatus.TimedOut, 41),
            Outcome(FarmJobStatus.Rejected, 42)
        };
        Assert.False(FarmNodeReliabilityEvaluator.Evaluate(failed, Now).IsQuarantined);

        var recovered = FarmNodeReliabilityEvaluator.Evaluate(
            failed.Append(Outcome(FarmJobStatus.Completed, 1)), Now);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.False(recovered.IsQuarantined);
    }

    [Fact]
    public void EvaluationUsesOnlyConfiguredRecentWindow()
    {
        var policy = new FarmNodeReliabilityPolicy(SampleSize: 3);
        var result = FarmNodeReliabilityEvaluator.Evaluate(
        [
            Outcome(FarmJobStatus.Failed, 1),
            Outcome(FarmJobStatus.Completed, 2),
            Outcome(FarmJobStatus.Completed, 3),
            Outcome(FarmJobStatus.Failed, 4)
        ], Now, policy);

        Assert.Equal(3, result.SampleSize);
        Assert.Equal(2, result.SuccessfulJobs);
        Assert.Equal(1, result.FailedJobs);
    }

    [Fact]
    public void PolicyRejectsUnsafeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FarmNodeReliabilityPolicy(SampleSize: 2).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FarmNodeReliabilityPolicy(ConsecutiveFailureThreshold: 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FarmNodeReliabilityPolicy(QuarantineMinutes: 0).Validate());
    }

    private static FarmNodeJobOutcome Outcome(FarmJobStatus status, int minutesAgo) =>
        new(status, Now.AddMinutes(-minutesAgo));
}
