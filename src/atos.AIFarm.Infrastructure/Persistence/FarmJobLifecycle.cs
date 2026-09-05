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
    internal static readonly TimeSpan CancellationGracePeriod = TimeSpan.FromSeconds(10);

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
        // O teto de execução é o TimeoutSeconds do manifesto/catálogo,
        // limitado a 120s (ADR 0043). A query corta pelo maior teto e o
        // refinamento por workload acontece em memória, workload a workload.
        var maximumTimeoutSeconds = FarmWorkloadCatalog.Definitions
            .Max(definition => Math.Clamp(definition.TimeoutSeconds, 5, 120));
        var executionDeadline = now.AddSeconds(-maximumTimeoutSeconds);
        var timeoutByVersion = FarmWorkloadCatalog.Definitions.ToDictionary(
            definition => definition.Version,
            definition => Math.Clamp(definition.TimeoutSeconds, 5, 120),
            StringComparer.Ordinal);
        var cancellationDeadline = now.Subtract(CancellationGracePeriod);
        var query = db.FarmJobs.Where(x =>
            ActiveStatuses.Contains(x.Status) &&
            ((x.CancellationRequestedAtUtc != null &&
              x.CancellationRequestedAtUtc <= cancellationDeadline) ||
             (ExecutingStatuses.Contains(x.Status) &&
              x.AgentLeaseExpiresAtUtc != null &&
              x.AgentLeaseExpiresAtUtc <= now) ||
             (ExecutingStatuses.Contains(x.Status) &&
              x.StartedAtUtc != null &&
              x.StartedAtUtc <= executionDeadline) ||
             (x.ManifestExpiresAtUtc != null && x.ManifestExpiresAtUtc <= now) ||
             (x.ManifestExpiresAtUtc == null && x.CreatedAtUtc <= legacyDeadline)));
        if (accountId is not null) query = query.Where(x => x.AccountId == accountId);
        if (nodeId is not null) query = query.Where(x => x.NodeId == nodeId);

        var candidates = await query
            .Select(x => new
            {
                x.Id,
                x.Status,
                x.WorkloadVersion,
                x.StartedAtUtc,
                x.ManifestExpiresAtUtc,
                x.CreatedAtUtc,
                x.CancellationRequestedAtUtc,
                x.AgentLeaseExpiresAtUtc
            })
            .ToArrayAsync(cancellationToken);
        var expired = 0;
        foreach (var candidate in candidates)
        {
            var workloadTimeoutSeconds = timeoutByVersion.GetValueOrDefault(candidate.WorkloadVersion, 30);
            var cancellationRequested = candidate.CancellationRequestedAtUtc is not null &&
                candidate.CancellationRequestedAtUtc <= cancellationDeadline;
            var leaseExpired = candidate.AgentLeaseExpiresAtUtc is not null &&
                candidate.AgentLeaseExpiresAtUtc <= now;
            var runtimeExceeded = ExecutingStatuses.Contains(candidate.Status, StringComparer.Ordinal) &&
                candidate.StartedAtUtc is not null &&
                candidate.StartedAtUtc <= now.AddSeconds(-workloadTimeoutSeconds);
            var manifestExpired =
                (candidate.ManifestExpiresAtUtc is not null && candidate.ManifestExpiresAtUtc <= now) ||
                (candidate.ManifestExpiresAtUtc is null && candidate.CreatedAtUtc <= legacyDeadline);
            // A query cortou pelo MAIOR teto; um workload de teto maior ainda
            // dentro da própria janela não é encerrado aqui.
            if (!cancellationRequested && !leaseExpired && !runtimeExceeded && !manifestExpired)
                continue;
            var terminalStatus = cancellationRequested
                ? FarmJobStatus.Cancelled
                : FarmJobStatus.TimedOut;
            var detail = cancellationRequested
                ? "Cancelamento remoto não foi confirmado dentro do prazo; o lease foi encerrado pelo servidor."
                : leaseExpired
                    ? "O lease do Compute Agent expirou durante a execução."
                    : runtimeExceeded
                        ? $"O runtime excedeu o timeout autorizado de {workloadTimeoutSeconds} segundos."
                        : "A validade do manifesto terminou antes da conclusão do Job.";
            if (await TryTerminateAsync(
                    db,
                    candidate.Id,
                    terminalStatus,
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

        // Cancelamento descarta qualquer evidência parcial já enviada: um Job
        // Cancelled nunca carrega resultado, telemetria ou saída tipada.
        var updated = terminalStatus == FarmJobStatus.Cancelled
            ? await db.FarmJobs
                .Where(x => x.Id == jobId && ActiveStatuses.Contains(x.Status))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.Status, terminalStatus.ToString())
                        .SetProperty(x => x.FailureReason, detail)
                        .SetProperty(x => x.FinishedAtUtc, now)
                        .SetProperty(x => x.ResultDigest, (string?)null)
                        .SetProperty(x => x.DurationMilliseconds, (long?)null)
                        .SetProperty(x => x.WorkerPeakMemoryBytes, (long?)null)
                        .SetProperty(x => x.WorkerProcessorTimeMilliseconds, (long?)null)
                        .SetProperty(x => x.WorkerEnforcement, (string?)null)
                        .SetProperty(x => x.OutputVersion, (string?)null)
                        .SetProperty(x => x.OutputPayload, (string?)null)
                        .SetProperty(x => x.OutputSizeBytes, (int?)null)
                        .SetProperty(x => x.OutputDigest, (string?)null),
                    cancellationToken)
            : await db.FarmJobs
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
