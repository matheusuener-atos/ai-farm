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

    /// <summary>
    /// Monta o mesmo contrato a partir de agregados por dia/rota/status já
    /// calculados no banco, mais amostras leves de duração da janela atual.
    /// Deve produzir resultado idêntico ao <see cref="Build"/> — coberto por
    /// teste de paridade.
    /// </summary>
    public static AiUsageAnalytics FromAggregates(
        int days,
        DateTimeOffset nowUtc,
        int conversationCount,
        long previousActualCostMinor,
        IReadOnlyCollection<AiUsageDailyAggregate> daily,
        IReadOnlyCollection<AiUsageRouteAggregate> routes,
        IReadOnlyCollection<AiUsageStatusAggregate> statuses,
        IReadOnlyCollection<AiUsageDurationSample> durationSamples)
    {
        if (days is < 1 or > 365)
            throw new ArgumentOutOfRangeException(nameof(days), "O período deve ficar entre 1 e 365 dias.");

        var now = nowUtc.ToUniversalTime();
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var from = today.AddDays(-(days - 1));
        var executionCount = daily.Sum(x => x.ExecutionCount);
        var completedCount = daily.Sum(x => x.SuccessCount);
        var totals = new AiUsageTotals(
            executionCount,
            conversationCount,
            daily.Sum(x => x.InputUnits),
            daily.Sum(x => x.OutputUnits),
            daily.Sum(x => x.EstimatedCostMinor),
            daily.Sum(x => x.ActualCostMinor));
        var successRate = executionCount == 0
            ? 0
            : decimal.Round(completedCount * 100m / executionCount, 1);
        var validSamples = durationSamples
            .Where(x => x.FinishedAtUtc >= x.StartedAtUtc)
            .ToArray();
        decimal? averageDuration = validSamples.Length == 0
            ? null
            : decimal.Round(
                (decimal)validSamples.Average(x => (x.FinishedAtUtc - x.StartedAtUtc).TotalMilliseconds),
                1);
        decimal? costChange = previousActualCostMinor == 0
            ? null
            : decimal.Round(
                (totals.ActualCostMinor - previousActualCostMinor) * 100m / previousActualCostMinor,
                1);

        var byDate = daily.ToDictionary(x => x.DateUtc);
        var dailyPoints = Enumerable.Range(0, days)
            .Select(offset => DateOnly.FromDateTime(from.AddDays(offset).UtcDateTime))
            .Select(date =>
            {
                var aggregate = byDate.GetValueOrDefault(date);
                return new AiUsageDailyPoint(
                    date,
                    aggregate?.ExecutionCount ?? 0,
                    aggregate?.SuccessCount ?? 0,
                    (aggregate?.ExecutionCount ?? 0) - (aggregate?.SuccessCount ?? 0),
                    aggregate?.InputUnits ?? 0,
                    aggregate?.OutputUnits ?? 0,
                    aggregate?.EstimatedCostMinor ?? 0,
                    aggregate?.ActualCostMinor ?? 0);
            })
            .ToArray();
        var durationsByRoute = validSamples
            .GroupBy(x => (x.Provider, x.Model))
            .ToDictionary(
                x => x.Key,
                x => decimal.Round(
                    (decimal)x.Average(sample => (sample.FinishedAtUtc - sample.StartedAtUtc).TotalMilliseconds),
                    1));
        var routeBreakdown = routes
            .Select(route => new AiUsageRouteBreakdown(
                route.Provider,
                route.Model,
                route.ExecutionCount,
                route.SuccessCount,
                route.ExecutionCount - route.SuccessCount,
                route.InputUnits,
                route.OutputUnits,
                route.EstimatedCostMinor,
                route.ActualCostMinor,
                durationsByRoute.TryGetValue((route.Provider, route.Model), out var routeAverage)
                    ? routeAverage
                    : null))
            .OrderByDescending(x => x.ActualCostMinor)
            .ThenByDescending(x => x.ExecutionCount)
            .ThenBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var statusBreakdown = statuses
            .Select(status => new AiUsageStatusBreakdown(
                status.Status,
                status.ExecutionCount,
                executionCount == 0
                    ? 0
                    : decimal.Round(status.ExecutionCount * 100m / executionCount, 1)))
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
            previousActualCostMinor,
            costChange,
            dailyPoints,
            routeBreakdown,
            statusBreakdown);
    }

    private static bool IsCompleted(AiExecutionSummary execution) =>
        string.Equals(execution.Status, "Completed", StringComparison.OrdinalIgnoreCase);
}

public sealed record AiUsageDailyAggregate(
    DateOnly DateUtc,
    int ExecutionCount,
    int SuccessCount,
    long InputUnits,
    long OutputUnits,
    long EstimatedCostMinor,
    long ActualCostMinor);

public sealed record AiUsageRouteAggregate(
    string Provider,
    string Model,
    int ExecutionCount,
    int SuccessCount,
    long InputUnits,
    long OutputUnits,
    long EstimatedCostMinor,
    long ActualCostMinor);

public sealed record AiUsageStatusAggregate(string Status, int ExecutionCount);

public sealed record AiUsageDurationSample(
    string Provider,
    string Model,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc);
