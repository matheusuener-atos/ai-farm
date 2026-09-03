namespace atos.AIFarm.Core.Application;

public enum FarmNodeReliabilityBand
{
    Unproven,
    Watch,
    Stable,
    Trusted,
    Quarantined
}

public sealed record FarmNodeReliabilityPolicy(
    int SampleSize = 20,
    int PriorSuccesses = 4,
    int PriorFailures = 1,
    int ConsecutiveFailureThreshold = 3,
    int QuarantineMinutes = 30)
{
    public static FarmNodeReliabilityPolicy Default { get; } = new();

    public FarmNodeReliabilityPolicy Validate()
    {
        if (SampleSize is < 3 or > 100)
            throw new ArgumentOutOfRangeException(nameof(SampleSize));
        if (PriorSuccesses is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(PriorSuccesses));
        if (PriorFailures is < 0 or > 100 || PriorSuccesses + PriorFailures == 0)
            throw new ArgumentOutOfRangeException(nameof(PriorFailures));
        if (ConsecutiveFailureThreshold is < 2 or > 10 || ConsecutiveFailureThreshold > SampleSize)
            throw new ArgumentOutOfRangeException(nameof(ConsecutiveFailureThreshold));
        if (QuarantineMinutes is < 1 or > 1_440)
            throw new ArgumentOutOfRangeException(nameof(QuarantineMinutes));
        return this;
    }
}

public sealed record FarmNodeJobOutcome(
    FarmJobStatus Status,
    DateTimeOffset FinishedAtUtc);

public sealed record FarmNodeReliabilitySnapshot(
    int Score,
    FarmNodeReliabilityBand Band,
    int SampleSize,
    int SuccessfulJobs,
    int FailedJobs,
    int ConsecutiveFailures,
    DateTimeOffset? LastOutcomeAtUtc,
    DateTimeOffset? QuarantinedUntilUtc)
{
    public bool IsQuarantined => Band == FarmNodeReliabilityBand.Quarantined;
}

public static class FarmNodeReliabilityEvaluator
{
    public static FarmNodeReliabilitySnapshot Evaluate(
        IEnumerable<FarmNodeJobOutcome> outcomes,
        DateTimeOffset now,
        FarmNodeReliabilityPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var activePolicy = (policy ?? FarmNodeReliabilityPolicy.Default).Validate();
        var utcNow = now.ToUniversalTime();
        var sample = outcomes
            .Where(x => IsObserved(x.Status))
            .OrderByDescending(x => x.FinishedAtUtc)
            .Take(activePolicy.SampleSize)
            .ToArray();
        var successes = sample.Count(x => x.Status == FarmJobStatus.Completed);
        var failures = sample.Length - successes;
        var consecutiveFailures = sample
            .TakeWhile(x => x.Status != FarmJobStatus.Completed)
            .Count();
        var quarantineDeadline = consecutiveFailures >= activePolicy.ConsecutiveFailureThreshold
            ? sample[0].FinishedAtUtc.ToUniversalTime().AddMinutes(activePolicy.QuarantineMinutes)
            : null as DateTimeOffset?;
        var quarantined = quarantineDeadline > utcNow;
        var score = (int)decimal.Round(
            (successes + activePolicy.PriorSuccesses) * 100m /
            (sample.Length + activePolicy.PriorSuccesses + activePolicy.PriorFailures),
            0,
            MidpointRounding.AwayFromZero);
        var band = quarantined
            ? FarmNodeReliabilityBand.Quarantined
            : sample.Length == 0
                ? FarmNodeReliabilityBand.Unproven
                : score >= 90
                    ? FarmNodeReliabilityBand.Trusted
                    : score >= 70
                        ? FarmNodeReliabilityBand.Stable
                        : FarmNodeReliabilityBand.Watch;

        return new FarmNodeReliabilitySnapshot(
            score,
            band,
            sample.Length,
            successes,
            failures,
            consecutiveFailures,
            sample.FirstOrDefault()?.FinishedAtUtc.ToUniversalTime(),
            quarantineDeadline);
    }

    private static bool IsObserved(FarmJobStatus status) => status is
        FarmJobStatus.Completed or
        FarmJobStatus.Failed or
        FarmJobStatus.TimedOut or
        FarmJobStatus.Rejected;
}
