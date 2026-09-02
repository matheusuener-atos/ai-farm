namespace atos.AIFarm.Core.Application;

public sealed record FarmAnalyticsJob(
    Guid Id,
    string Workload,
    string WorkloadVersion,
    FarmJobStatus Status,
    long ProcessingPointsMilli,
    long? DurationMilliseconds,
    long? PeakMemoryBytes,
    long? ProcessorTimeMilliseconds,
    string? Enforcement,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? FinishedAtUtc);

public sealed record FarmAnalyticsNode(
    Guid Id,
    string Name,
    FarmNodeStatus Status,
    FarmVerificationLevel VerificationLevel,
    FarmNodeConnectionKind ConnectionKind,
    bool HeartbeatFresh,
    bool AcceptJobs,
    int LogicalProcessors,
    long MemoryBytes,
    DateTimeOffset LastSeenAtUtc,
    string? AgentVersion);

public sealed record FarmEconomicsPipelineAnalytics(
    int ScenarioCount,
    int RewardProposalCount,
    long ProposedProcessingPointsMilli,
    decimal RecommendedRewardBrl);

public sealed record FarmAnalyticsDailyPoint(
    DateOnly DateUtc,
    int JobCount,
    int CompletedCount,
    int FailedCount,
    long VerifiedProcessingPointsMilli,
    long DurationMilliseconds);

public sealed record FarmAnalyticsWorkloadBreakdown(
    string Workload,
    string Version,
    int JobCount,
    int CompletedCount,
    int FailedCount,
    long VerifiedProcessingPointsMilli,
    decimal SuccessRatePercent,
    decimal? AverageDurationMilliseconds);

public sealed record FarmAnalyticsStatusBreakdown(
    FarmJobStatus Status,
    int JobCount,
    decimal Percentage);

public sealed record FarmAnalytics(
    int Days,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int JobCount,
    int ActiveJobCount,
    int CompletedJobCount,
    int FailedJobCount,
    decimal SuccessRatePercent,
    long VerifiedProcessingPointsMilli,
    decimal? AverageDurationMilliseconds,
    decimal ProcessingPointsPerHour,
    long PeakMemoryBytes,
    long ProcessorTimeMilliseconds,
    long PreviousPeriodProcessingPointsMilli,
    decimal? ProcessingPointsChangePercent,
    FarmAnalyticsNode? Node,
    FarmEconomicsPipelineAnalytics Economics,
    IReadOnlyList<FarmAnalyticsDailyPoint> Daily,
    IReadOnlyList<FarmAnalyticsWorkloadBreakdown> Workloads,
    IReadOnlyList<FarmAnalyticsStatusBreakdown> Statuses);

public interface IFarmAnalyticsQuery
{
    Task<FarmAnalytics> GetAsync(
        int days = 30,
        CancellationToken cancellationToken = default);
}

public static class FarmAnalyticsBuilder
{
    public static FarmAnalytics Build(
        IReadOnlyCollection<FarmAnalyticsJob> jobs,
        FarmAnalyticsNode? node,
        FarmEconomicsPipelineAnalytics economics,
        int days,
        DateTimeOffset nowUtc)
    {
        if (days is < 1 or > 365)
            throw new ArgumentOutOfRangeException(nameof(days), "O período deve ficar entre 1 e 365 dias.");

        var now = nowUtc.ToUniversalTime();
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var from = today.AddDays(-(days - 1));
        var previousFrom = from.AddDays(-days);
        var current = jobs.Where(x => x.CreatedAtUtc >= from && x.CreatedAtUtc <= now).ToArray();
        var previous = jobs.Where(x => x.CreatedAtUtc >= previousFrom && x.CreatedAtUtc < from).ToArray();
        var completed = current.Where(x => x.Status == FarmJobStatus.Completed).ToArray();
        var failed = current.Where(x => IsFailed(x.Status)).ToArray();
        var terminalCount = completed.Length + failed.Length;
        var successRate = terminalCount == 0
            ? 0
            : decimal.Round(completed.Length * 100m / terminalCount, 1);
        var durations = current.Where(x => x.DurationMilliseconds > 0)
            .Select(x => x.DurationMilliseconds!.Value).ToArray();
        decimal? averageDuration = durations.Length == 0
            ? null
            : decimal.Round((decimal)durations.Average(), 1);
        var verifiedPoints = completed.Sum(x => x.ProcessingPointsMilli);
        var completedDuration = completed.Where(x => x.DurationMilliseconds > 0)
            .Sum(x => x.DurationMilliseconds!.Value);
        var pointsPerHour = completedDuration == 0
            ? 0
            : decimal.Round(
                verifiedPoints / 1_000m / (completedDuration / 3_600_000m),
                3);
        var previousPoints = previous.Where(x => x.Status == FarmJobStatus.Completed)
            .Sum(x => x.ProcessingPointsMilli);
        decimal? pointsChange = previousPoints == 0
            ? null
            : decimal.Round((verifiedPoints - previousPoints) * 100m / previousPoints, 1);
        var byDate = current.GroupBy(x => DateOnly.FromDateTime(x.CreatedAtUtc.UtcDateTime))
            .ToDictionary(x => x.Key, x => x.ToArray());
        var daily = Enumerable.Range(0, days)
            .Select(offset => DateOnly.FromDateTime(from.AddDays(offset).UtcDateTime))
            .Select(date =>
            {
                var rows = byDate.GetValueOrDefault(date) ?? [];
                return new FarmAnalyticsDailyPoint(
                    date,
                    rows.Length,
                    rows.Count(x => x.Status == FarmJobStatus.Completed),
                    rows.Count(x => IsFailed(x.Status)),
                    rows.Where(x => x.Status == FarmJobStatus.Completed).Sum(x => x.ProcessingPointsMilli),
                    rows.Where(x => x.DurationMilliseconds > 0).Sum(x => x.DurationMilliseconds!.Value));
            }).ToArray();
        var workloads = current.GroupBy(x => new { x.Workload, x.WorkloadVersion })
            .Select(group =>
            {
                var done = group.Count(x => x.Status == FarmJobStatus.Completed);
                var failures = group.Count(x => IsFailed(x.Status));
                var terminal = done + failures;
                var workloadDurations = group.Where(x => x.DurationMilliseconds > 0)
                    .Select(x => x.DurationMilliseconds!.Value).ToArray();
                return new FarmAnalyticsWorkloadBreakdown(
                    group.Key.Workload,
                    group.Key.WorkloadVersion,
                    group.Count(),
                    done,
                    failures,
                    group.Where(x => x.Status == FarmJobStatus.Completed).Sum(x => x.ProcessingPointsMilli),
                    terminal == 0 ? 0 : decimal.Round(done * 100m / terminal, 1),
                    workloadDurations.Length == 0 ? null : decimal.Round((decimal)workloadDurations.Average(), 1));
            })
            .OrderByDescending(x => x.VerifiedProcessingPointsMilli)
            .ThenByDescending(x => x.JobCount)
            .ToArray();
        var statuses = current.GroupBy(x => x.Status)
            .Select(group => new FarmAnalyticsStatusBreakdown(
                group.Key,
                group.Count(),
                current.Length == 0 ? 0 : decimal.Round(group.Count() * 100m / current.Length, 1)))
            .OrderByDescending(x => x.JobCount)
            .ThenBy(x => x.Status)
            .ToArray();

        return new FarmAnalytics(
            days,
            from,
            now,
            current.Length,
            current.Count(x => !IsTerminal(x.Status)),
            completed.Length,
            failed.Length,
            successRate,
            verifiedPoints,
            averageDuration,
            pointsPerHour,
            current.Select(x => x.PeakMemoryBytes ?? 0).DefaultIfEmpty().Max(),
            current.Sum(x => x.ProcessorTimeMilliseconds ?? 0),
            previousPoints,
            pointsChange,
            node,
            economics,
            daily,
            workloads,
            statuses);
    }

    private static bool IsTerminal(FarmJobStatus status) =>
        status == FarmJobStatus.Completed || IsFailed(status);

    private static bool IsFailed(FarmJobStatus status) => status is
        FarmJobStatus.Failed or FarmJobStatus.Cancelled or FarmJobStatus.TimedOut or FarmJobStatus.Rejected;
}
