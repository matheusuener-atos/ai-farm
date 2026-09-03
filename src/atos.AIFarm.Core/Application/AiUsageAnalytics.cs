namespace atos.AIFarm.Core.Application;

public sealed record AiUsageDailyPoint(
    DateOnly DateUtc,
    int ExecutionCount,
    int SuccessCount,
    int FailureCount,
    long InputUnits,
    long OutputUnits,
    long EstimatedCostMinor,
    long ActualCostMinor);

public sealed record AiUsageRouteBreakdown(
    string Provider,
    string Model,
    int ExecutionCount,
    int SuccessCount,
    int FailureCount,
    long InputUnits,
    long OutputUnits,
    long EstimatedCostMinor,
    long ActualCostMinor,
    decimal? AverageDurationMilliseconds);

public sealed record AiUsageStatusBreakdown(
    string Status,
    int ExecutionCount,
    decimal Percentage);

public sealed record AiUsageAnalytics(
    int Days,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    AiUsageTotals Totals,
    decimal SuccessRatePercent,
    decimal? AverageDurationMilliseconds,
    long PreviousPeriodActualCostMinor,
    decimal? ActualCostChangePercent,
    IReadOnlyList<AiUsageDailyPoint> Daily,
    IReadOnlyList<AiUsageRouteBreakdown> Routes,
    IReadOnlyList<AiUsageStatusBreakdown> Statuses);

public static class AiUsageAnalyticsBuilder
{
    public static AiUsageAnalytics Build(
        IReadOnlyCollection<AiExecutionSummary> executions,
        int days,
        DateTimeOffset nowUtc)
    {
        if (days is < 1 or > 365)
            throw new ArgumentOutOfRangeException(nameof(days), "O período deve ficar entre 1 e 365 dias.");

        var now = nowUtc.ToUniversalTime();
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var from = today.AddDays(-(days - 1));
        var previousFrom = from.AddDays(-days);
        var current = executions
            .Where(x => x.StartedAtUtc >= from && x.StartedAtUtc <= now)
            .ToArray();
        var previous = executions
            .Where(x => x.StartedAtUtc >= previousFrom && x.StartedAtUtc < from)
            .ToArray();
        var completed = current.Where(IsCompleted).ToArray();
        var totals = new AiUsageTotals(
            current.Length,
            current.Select(x => x.ConversationId).Distinct().Count(),
            current.Sum(x => x.InputUnits),
            current.Sum(x => x.OutputUnits),
            current.Sum(x => x.EstimatedCostMinor),
            current.Sum(x => x.ActualCostMinor));
        var successRate = current.Length == 0
            ? 0
            : decimal.Round(completed.Length * 100m / current.Length, 1);
        var durations = current
            .Where(x => x.FinishedAtUtc >= x.StartedAtUtc)
            .Select(x => (x.FinishedAtUtc!.Value - x.StartedAtUtc).TotalMilliseconds)
            .ToArray();
        decimal? averageDuration = durations.Length == 0
            ? null
            : decimal.Round((decimal)durations.Average(), 1);
        var previousActualCost = previous.Sum(x => x.ActualCostMinor);
        decimal? costChange = previousActualCost == 0
            ? null
            : decimal.Round(
                (totals.ActualCostMinor - previousActualCost) * 100m / previousActualCost,
                1);

        var byDate = current
            .GroupBy(x => DateOnly.FromDateTime(x.StartedAtUtc.UtcDateTime))
            .ToDictionary(x => x.Key, x => x.ToArray());
        var daily = Enumerable.Range(0, days)
            .Select(offset => DateOnly.FromDateTime(from.AddDays(offset).UtcDateTime))
            .Select(date =>
            {
                var rows = byDate.GetValueOrDefault(date) ?? [];
                return new AiUsageDailyPoint(
                    date,
                    rows.Length,
                    rows.Count(IsCompleted),
                    rows.Count(x => !IsCompleted(x)),
                    rows.Sum(x => x.InputUnits),
                    rows.Sum(x => x.OutputUnits),
                    rows.Sum(x => x.EstimatedCostMinor),
                    rows.Sum(x => x.ActualCostMinor));
            })
            .ToArray();
        var routes = current
            .GroupBy(x => new { x.Provider, x.Model })
            .Select(group =>
            {
                var routeDurations = group
                    .Where(x => x.FinishedAtUtc >= x.StartedAtUtc)
                    .Select(x => (x.FinishedAtUtc!.Value - x.StartedAtUtc).TotalMilliseconds)
                    .ToArray();
                return new AiUsageRouteBreakdown(
                    group.Key.Provider,
                    group.Key.Model,
                    group.Count(),
                    group.Count(IsCompleted),
                    group.Count(x => !IsCompleted(x)),
                    group.Sum(x => x.InputUnits),
                    group.Sum(x => x.OutputUnits),
                    group.Sum(x => x.EstimatedCostMinor),
                    group.Sum(x => x.ActualCostMinor),
                    routeDurations.Length == 0
                        ? null
                        : decimal.Round((decimal)routeDurations.Average(), 1));
            })
            .OrderByDescending(x => x.ActualCostMinor)
            .ThenByDescending(x => x.ExecutionCount)
            .ThenBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var statuses = current
            .GroupBy(x => x.Status)
            .Select(group => new AiUsageStatusBreakdown(
                group.Key,
                group.Count(),
                current.Length == 0
                    ? 0
                    : decimal.Round(group.Count() * 100m / current.Length, 1)))
            .OrderByDescending(x => x.ExecutionCount)
            .ThenBy(x => x.Status, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AiUsageAnalytics(
            days,
            from,
            now,
            totals,
            successRate,
            averageDuration,
            previousActualCost,
            costChange,
            daily,
            routes,
            statuses);
    }

    private static bool IsCompleted(AiExecutionSummary execution) =>
        string.Equals(execution.Status, "Completed", StringComparison.OrdinalIgnoreCase);
}
