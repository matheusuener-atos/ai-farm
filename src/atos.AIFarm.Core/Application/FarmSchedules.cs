namespace atos.AIFarm.Core.Application;

public enum FarmScheduleStatus { Active, Paused }
public enum FarmScheduleTriggerStatus { Dispatching, Submitted, Deferred, Failed }

public sealed record FarmScheduleRequest(
    string Name,
    string WorkloadId,
    int IntervalMinutes,
    DateTimeOffset? FirstRunAtUtc = null)
{
    public FarmScheduleRequest Validate(DateTimeOffset nowUtc)
    {
        var name = Name?.Trim() ?? string.Empty;
        if (name.Length is < 3 or > 80)
            throw new ArgumentException("O nome deve conter entre 3 e 80 caracteres.", nameof(Name));
        if (name.Any(char.IsControl))
            throw new ArgumentException("O nome contém caracteres inválidos.", nameof(Name));
        var workload = FarmWorkloadCatalog.GetRequired(WorkloadId);
        if (workload.InputSchema is not null)
            throw new ArgumentException("Demandas com entrada não podem ser agendadas sem payload.", nameof(WorkloadId));
        if (IntervalMinutes is < 5 or > 10_080)
            throw new ArgumentOutOfRangeException(nameof(IntervalMinutes),
                "O intervalo deve ficar entre 5 minutos e 7 dias.");
        var firstRun = (FirstRunAtUtc ?? nowUtc).ToUniversalTime();
        if (firstRun < nowUtc.AddMinutes(-1) || firstRun > nowUtc.AddDays(30))
            throw new ArgumentOutOfRangeException(nameof(FirstRunAtUtc),
                "A primeira execução deve ficar entre agora e os próximos 30 dias.");
        return this with { Name = name, WorkloadId = WorkloadId.Trim(), FirstRunAtUtc = firstRun };
    }
}

public sealed record FarmScheduleSnapshot(
    Guid Id,
    string Name,
    string WorkloadId,
    string Workload,
    string WorkloadVersion,
    int IntervalMinutes,
    FarmScheduleStatus Status,
    DateTimeOffset NextRunAtUtc,
    DateTimeOffset? LastTriggeredAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record FarmScheduleTriggerSnapshot(
    Guid Id,
    Guid ScheduleId,
    DateTimeOffset ScheduledForUtc,
    FarmScheduleTriggerStatus Status,
    Guid? JobId,
    string? Detail,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? FinishedAtUtc);

public sealed record FarmScheduleDashboard(
    IReadOnlyList<FarmScheduleSnapshot> Schedules,
    IReadOnlyList<FarmScheduleTriggerSnapshot> RecentTriggers)
{
    public int ActiveCount => Schedules.Count(x => x.Status == FarmScheduleStatus.Active);
    public int PausedCount => Schedules.Count(x => x.Status == FarmScheduleStatus.Paused);
    public int FailureCount => RecentTriggers.Count(x =>
        x.Status is FarmScheduleTriggerStatus.Deferred or FarmScheduleTriggerStatus.Failed);
}

public interface IFarmScheduleService
{
    Task<FarmScheduleDashboard> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<FarmScheduleSnapshot> CreateAsync(FarmScheduleRequest request, CancellationToken cancellationToken = default);
    Task<FarmScheduleSnapshot> SetStatusAsync(Guid scheduleId, FarmScheduleStatus status, CancellationToken cancellationToken = default);
    Task<FarmScheduleTriggerSnapshot> TriggerNowAsync(Guid scheduleId, CancellationToken cancellationToken = default);
}

public interface IFarmScheduleDispatcher
{
    Task<int> DispatchDueAsync(CancellationToken cancellationToken = default);
}

public static class FarmScheduleRecurrence
{
    public static DateTimeOffset Next(DateTimeOffset scheduledForUtc, int intervalMinutes, DateTimeOffset nowUtc)
    {
        if (intervalMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(intervalMinutes));
        var next = scheduledForUtc.ToUniversalTime().AddMinutes(intervalMinutes);
        var now = nowUtc.ToUniversalTime();
        if (next > now) return next;
        var skipped = (long)Math.Floor((now - next).TotalMinutes / intervalMinutes) + 1;
        return next.AddMinutes(skipped * intervalMinutes);
    }
}
