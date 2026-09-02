using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

internal sealed class PostgresFarmScheduleService(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    PostgresFarmLabService farmLab,
    TimeProvider timeProvider) : IFarmScheduleService, IFarmScheduleDispatcher
{
    private const int RecentTriggerLimit = 50;

    public async Task<FarmScheduleDashboard> GetDashboardAsync(
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var schedules = await db.FarmSchedules.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId)
            .OrderBy(x => x.Status)
            .ThenBy(x => x.NextRunAtUtc)
            .ToArrayAsync(cancellationToken);
        var triggers = await db.FarmScheduleTriggers.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(RecentTriggerLimit)
            .ToArrayAsync(cancellationToken);
        return new FarmScheduleDashboard(
            schedules.Select(ToSnapshot).ToArray(),
            triggers.Select(ToSnapshot).ToArray());
    }

    public async Task<FarmScheduleSnapshot> CreateAsync(
        FarmScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = timeProvider.GetUtcNow();
        var validated = request.Validate(now);
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var definition = FarmWorkloadCatalog.GetRequired(validated.WorkloadId);
        var record = new FarmScheduleRecord
        {
            Id = Guid.NewGuid(),
            AccountId = identity.AccountId,
            Name = validated.Name,
            NormalizedName = NormalizeName(validated.Name),
            WorkloadId = definition.Id,
            IntervalMinutes = validated.IntervalMinutes,
            Status = FarmScheduleStatus.Active.ToString(),
            NextRunAtUtc = validated.FirstRunAtUtc!.Value,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.FarmSchedules.Add(record);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception)
        {
            throw new InvalidOperationException(
                "Já existe um agendamento com esse nome nesta conta.", exception);
        }
        return ToSnapshot(record);
    }

    public async Task<FarmScheduleSnapshot> SetStatusAsync(
        Guid scheduleId,
        FarmScheduleStatus status,
        CancellationToken cancellationToken = default)
    {
        if (status is not (FarmScheduleStatus.Active or FarmScheduleStatus.Paused))
            throw new ArgumentOutOfRangeException(nameof(status));
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await db.FarmSchedules.SingleOrDefaultAsync(
            x => x.Id == scheduleId && x.AccountId == identity.AccountId,
            cancellationToken)
            ?? throw new InvalidOperationException("Agendamento não encontrado para esta conta.");
        var now = timeProvider.GetUtcNow();
        record.Status = status.ToString();
        if (status == FarmScheduleStatus.Active && record.NextRunAtUtc < now)
            record.NextRunAtUtc = now;
        record.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToSnapshot(record);
    }

    public async Task<FarmScheduleTriggerSnapshot> TriggerNowAsync(
        Guid scheduleId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var schedule = await db.FarmSchedules.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == scheduleId && x.AccountId == identity.AccountId,
            cancellationToken)
            ?? throw new InvalidOperationException("Agendamento não encontrado para esta conta.");
        var trigger = new FarmScheduleTriggerRecord
        {
            Id = Guid.NewGuid(),
            AccountId = schedule.AccountId,
            ScheduleId = schedule.Id,
            ScheduledForUtc = timeProvider.GetUtcNow(),
            Status = FarmScheduleTriggerStatus.Dispatching.ToString(),
            CreatedAtUtc = timeProvider.GetUtcNow()
        };
        db.FarmScheduleTriggers.Add(trigger);
        await db.SaveChangesAsync(cancellationToken);
        await DispatchTriggerAsync(schedule, trigger, cancellationToken);
        return await LoadTriggerAsync(trigger.Id, cancellationToken);
    }

    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await RecoverAbandonedTriggersAsync(now, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var due = await db.FarmSchedules.AsNoTracking()
            .Where(x => x.Status == FarmScheduleStatus.Active.ToString() &&
                        x.NextRunAtUtc <= now)
            .OrderBy(x => x.NextRunAtUtc)
            .Take(50)
            .ToArrayAsync(cancellationToken);
        var dispatched = 0;
        foreach (var candidate in due)
        {
            var trigger = await TryClaimAsync(candidate, now, cancellationToken);
            if (trigger is null) continue;
            dispatched++;
            await DispatchTriggerAsync(candidate, trigger, cancellationToken);
        }
        return dispatched;
    }

    private async Task<FarmScheduleTriggerRecord?> TryClaimAsync(
        FarmScheduleRecord candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var next = FarmScheduleRecurrence.Next(
            candidate.NextRunAtUtc, candidate.IntervalMinutes, now);
        var claimed = await db.FarmSchedules
            .Where(x => x.Id == candidate.Id &&
                        x.Status == FarmScheduleStatus.Active.ToString() &&
                        x.NextRunAtUtc == candidate.NextRunAtUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.NextRunAtUtc, next)
                .SetProperty(x => x.LastTriggeredAtUtc, now)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        var trigger = new FarmScheduleTriggerRecord
        {
            Id = Guid.NewGuid(),
            AccountId = candidate.AccountId,
            ScheduleId = candidate.Id,
            ScheduledForUtc = candidate.NextRunAtUtc,
            Status = FarmScheduleTriggerStatus.Dispatching.ToString(),
            CreatedAtUtc = now
        };
        db.FarmScheduleTriggers.Add(trigger);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return trigger;
    }

    private async Task DispatchTriggerAsync(
        FarmScheduleRecord schedule,
        FarmScheduleTriggerRecord trigger,
        CancellationToken cancellationToken)
    {
        try
        {
            var job = await farmLab.RunScheduledWorkloadAsync(
                schedule.AccountId,
                schedule.WorkloadId,
                schedule.Id,
                trigger.ScheduledForUtc,
                cancellationToken);
            await FinishTriggerAsync(
                trigger.Id,
                FarmScheduleTriggerStatus.Submitted,
                job.Id,
                $"Job {job.Id:N} submetido ao pipeline verificável.",
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await FinishTriggerAsync(
                trigger.Id,
                FarmScheduleTriggerStatus.Deferred,
                null,
                Limit(exception.Message),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            await FinishTriggerAsync(
                trigger.Id,
                FarmScheduleTriggerStatus.Failed,
                null,
                Limit(exception.Message),
                CancellationToken.None);
        }
    }

    private async Task FinishTriggerAsync(
        Guid triggerId,
        FarmScheduleTriggerStatus status,
        Guid? jobId,
        string detail,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var trigger = await db.FarmScheduleTriggers.SingleAsync(
            x => x.Id == triggerId, cancellationToken);
        trigger.Status = status.ToString();
        trigger.JobId = jobId;
        trigger.Detail = detail;
        trigger.FinishedAtUtc = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecoverAbandonedTriggersAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.FarmScheduleTriggers
            .Where(x => x.Status == FarmScheduleTriggerStatus.Dispatching.ToString() &&
                        x.CreatedAtUtc < now.AddMinutes(-5))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, FarmScheduleTriggerStatus.Failed.ToString())
                .SetProperty(x => x.Detail, "Dispatcher interrompido antes de concluir o disparo.")
                .SetProperty(x => x.FinishedAtUtc, now), cancellationToken);
    }

    private async Task<FarmScheduleTriggerSnapshot> LoadTriggerAsync(
        Guid triggerId,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return ToSnapshot(await db.FarmScheduleTriggers.AsNoTracking()
            .SingleAsync(x => x.Id == triggerId, cancellationToken));
    }

    private static FarmScheduleSnapshot ToSnapshot(FarmScheduleRecord record)
    {
        var definition = FarmWorkloadCatalog.GetRequired(record.WorkloadId);
        return new FarmScheduleSnapshot(
            record.Id, record.Name, record.WorkloadId, definition.Workload,
            definition.Version, record.IntervalMinutes,
            Enum.Parse<FarmScheduleStatus>(record.Status), record.NextRunAtUtc,
            record.LastTriggeredAtUtc, record.CreatedAtUtc, record.UpdatedAtUtc);
    }

    private static FarmScheduleTriggerSnapshot ToSnapshot(FarmScheduleTriggerRecord record) =>
        new(record.Id, record.ScheduleId, record.ScheduledForUtc,
            Enum.Parse<FarmScheduleTriggerStatus>(record.Status), record.JobId,
            record.Detail, record.CreatedAtUtc, record.FinishedAtUtc);

    private static string NormalizeName(string value) => value.Trim().ToUpperInvariant();
    private static string Limit(string value) =>
        value.Length <= 500 ? value : value[..500];
}
