using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

internal static class FarmJobLifecycle
{
    internal static readonly string[] ActiveStatuses =
    [
        FarmJobStatus.Queued.ToString(),
        FarmJobStatus.Offered.ToString(),
        FarmJobStatus.Accepted.ToString(),
        FarmJobStatus.Running.ToString(),
        FarmJobStatus.Uploading.ToString(),
        FarmJobStatus.Verifying.ToString()
    ];
    private static readonly string[] ExecutingStatuses =
    [
        FarmJobStatus.Running.ToString(),
        FarmJobStatus.Uploading.ToString(),
        FarmJobStatus.Verifying.ToString()
    ];

    public static bool IsActive(FarmJobStatus status) =>
        ActiveStatuses.Contains(status.ToString(), StringComparer.Ordinal);

    public static async Task<int> ExpireAsync(
        AIFarmDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Guid? accountId = null,
        Guid? nodeId = null)
    {
        var legacyDeadline = now.AddMinutes(-10);
        var executionDeadline = now.AddSeconds(-30);
        var query = db.FarmJobs.Where(x =>
            ActiveStatuses.Contains(x.Status) &&
            ((ExecutingStatuses.Contains(x.Status) &&
              x.StartedAtUtc != null &&
              x.StartedAtUtc <= executionDeadline) ||
             (x.ManifestExpiresAtUtc != null && x.ManifestExpiresAtUtc <= now) ||
             (x.ManifestExpiresAtUtc == null && x.CreatedAtUtc <= legacyDeadline)));
        if (accountId is not null) query = query.Where(x => x.AccountId == accountId);
        if (nodeId is not null) query = query.Where(x => x.NodeId == nodeId);

        var candidates = await query
            .Select(x => new { x.Id, x.Status })
            .ToArrayAsync(cancellationToken);
        var expired = 0;
        foreach (var candidate in candidates)
        {
            var detail = ExecutingStatuses.Contains(candidate.Status, StringComparer.Ordinal)
                ? "O runtime excedeu o timeout autorizado de 30 segundos."
                : "A validade do manifesto terminou antes da conclusão do Job.";
            if (await TryTerminateAsync(
                    db,
                    candidate.Id,
                    FarmJobStatus.TimedOut,
                    detail,
                    now,
                    cancellationToken))
                expired++;
        }
        return expired;
    }

    public static async Task<bool> TryTerminateAsync(
        AIFarmDbContext db,
        Guid jobId,
        FarmJobStatus terminalStatus,
        string detail,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (IsActive(terminalStatus))
            throw new ArgumentOutOfRangeException(nameof(terminalStatus));

        var updated = await db.FarmJobs
            .Where(x => x.Id == jobId && ActiveStatuses.Contains(x.Status))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, terminalStatus.ToString())
                    .SetProperty(x => x.FailureReason, detail)
                    .SetProperty(x => x.FinishedAtUtc, now),
                cancellationToken);
        if (updated != 1) return false;

        var sequence = await db.FarmJobEvents
            .Where(x => x.JobId == jobId)
            .MaxAsync(x => (int?)x.Sequence, cancellationToken) ?? 0;
        db.FarmJobEvents.Add(new FarmJobEventRecord
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            Sequence = sequence + 1,
            Status = terminalStatus.ToString(),
            Detail = detail,
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
