using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresOperationalIncidentService(
    IDbContextFactory<AIFarmDbContext> dbFactory,
    IAIFarmIdentityAccessor identityAccessor,
    IAuditTrail auditTrail,
    IFarmLabService farmLab,
    IFarmCapacityQuery capacityQuery,
    TimeProvider timeProvider) : IOperationalIncidentService
{
    // Teto de varredura por fonte dentro da janela; ao atingi-lo o dashboard
    // reporta IsTruncated em vez de omitir silenciosamente (ADR 0029).
    private const int SourceScanLimit = 500;
    private const int AuditPageSize = 100;

    public async Task<IncidentDashboard> SearchAsync(
        IncidentQuery query,
        CancellationToken cancellationToken = default)
    {
        var normalized = query.Normalize();
        var (fromUtc, toUtc) = normalized.ResolveWindow(timeProvider.GetUtcNow());
        var (incidents, truncated) = await DetectAsync(fromUtc, toUtc, cancellationToken);
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var keys = incidents.Select(x => x.Key).ToArray();
        var acknowledgements = await db.OperationalIncidentAcknowledgements.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId && keys.Contains(x.IncidentKey))
            .ToDictionaryAsync(x => x.IncidentKey, x => x.AcknowledgedAtUtc, cancellationToken);
        var hydrated = incidents.Select(x => x with
        {
            AcknowledgedAtUtc = acknowledgements.TryGetValue(x.Key, out var acknowledgedAt)
                ? acknowledgedAt
                : null
        }).ToArray();
        var filtered = hydrated
            .Where(x => normalized.Severity is null || x.Severity == normalized.Severity)
            .Where(x => !normalized.OpenOnly || !x.IsAcknowledged)
            .Where(x => normalized.Search is null || Matches(x, normalized.Search))
            .OrderByDescending(x => x.Severity)
            .ThenByDescending(x => x.DetectedAtUtc)
            .ToArray();
        return new IncidentDashboard(
            filtered,
            hydrated.Length,
            hydrated.Count(x => !x.IsAcknowledged),
            hydrated.Count(x => x.Severity == IncidentSeverity.Critical),
            hydrated.Count(x => x.Severity == IncidentSeverity.Warning),
            hydrated.Count(x => x.IsAcknowledged),
            fromUtc,
            toUtc,
            truncated);
    }

    public async Task<bool> AcknowledgeAsync(
        string incidentKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(incidentKey) || incidentKey.Length > 200) return false;
        var now = timeProvider.GetUtcNow();
        var (incidents, _) = await DetectAsync(
            now.AddDays(-IncidentQuery.MaximumWindowDays),
            now,
            cancellationToken);
        if (!incidents.Any(x => x.Key == incidentKey)) return false;
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.OperationalIncidentAcknowledgements.AnyAsync(
            x => x.AccountId == identity.AccountId && x.IncidentKey == incidentKey,
            cancellationToken)) return true;
        db.OperationalIncidentAcknowledgements.Add(new OperationalIncidentAcknowledgementRecord
        {
            Id = Guid.NewGuid(),
            AccountId = identity.AccountId,
            IncidentKey = incidentKey,
            AcknowledgedAtUtc = timeProvider.GetUtcNow().ToUniversalTime()
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            return await db.OperationalIncidentAcknowledgements.AsNoTracking().AnyAsync(
                x => x.AccountId == identity.AccountId && x.IncidentKey == incidentKey,
                cancellationToken);
        }
        return true;
    }

    private async Task<(IReadOnlyList<OperationalIncidentSnapshot> Incidents, bool Truncated)> DetectAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var truncated = false;
        var incidents = new List<OperationalIncidentSnapshot>();

        truncated |= await AddIntegrityIncidentsAsync(incidents, fromUtc, toUtc, cancellationToken);
        truncated |= await AddIrisIncidentsAsync(incidents, fromUtc, toUtc, cancellationToken);
        truncated |= await AddFarmJobIncidentsAsync(incidents, fromUtc, toUtc, cancellationToken);

        var farm = await farmLab.GetDashboardAsync(cancellationToken);
        if (farm.Node.Status is FarmNodeStatus.Offline or FarmNodeStatus.Paused)
        {
            incidents.Add(New(
                $"node:{farm.Node.Id:N}:{farm.Node.Status}", IncidentSource.Node,
                farm.Node.Status == FarmNodeStatus.Offline ? IncidentSeverity.Critical : IncidentSeverity.Warning,
                $"NODE_{farm.Node.Status.ToString().ToUpperInvariant()}",
                $"Node {farm.Node.Status}",
                $"{farm.Node.Name} // último sinal {farm.Node.LastSeenAtUtc:O}",
                farm.Node.Id.ToString("N")[..8], farm.Node.LastSeenAtUtc));
        }
        var capacity = await capacityQuery.GetAsync(cancellationToken);
        incidents.AddRange(capacity.Nodes
            .Where(x => x.Reliability?.IsQuarantined == true)
            .Select(x => New(
                $"node:{x.Node.Id:N}:QUARANTINED:{x.Reliability!.QuarantinedUntilUtc:yyyyMMddHHmmss}",
                IncidentSource.Node,
                IncidentSeverity.Critical,
                "NODE_RELIABILITY_QUARANTINE",
                $"Node {x.Node.Name} em quarentena automática",
                $"Score {x.Reliability.Score}/100 // {x.Reliability.ConsecutiveFailures} falhas consecutivas // retorno {x.Reliability.QuarantinedUntilUtc:O}",
                x.Node.Id.ToString("N")[..8],
                x.Reliability.LastOutcomeAtUtc ?? capacity.CalculatedAtUtc)));
        return (incidents.GroupBy(x => x.Key).Select(x => x.First()).ToArray(), truncated);
    }

    private async Task<bool> AddIntegrityIncidentsAsync(
        List<OperationalIncidentSnapshot> incidents,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var scanned = 0;
        var maximumPages = SourceScanLimit / AuditPageSize;
        for (var page = 1; page <= maximumPages; page++)
        {
            var audit = await auditTrail.SearchAsync(
                new AuditQuery(FromUtc: fromUtc, ToUtc: toUtc, Page: page, PageSize: AuditPageSize),
                cancellationToken);
            incidents.AddRange(audit.Events
                .Where(x => x.Integrity == AuditIntegrityStatus.Invalid)
                .Select(x => New(
                    $"integrity:{x.Category}:{x.Id:N}", IncidentSource.Integrity,
                    IncidentSeverity.Critical, "INTEGRITY_FAILURE",
                    $"Falha de integridade em {x.Category}",
                    $"{x.Action} // {x.Description}", x.Reference, x.OccurredAtUtc)));
            scanned += audit.Events.Count;
            if (audit.Events.Count < AuditPageSize)
            {
                return audit.IsScanTruncated;
            }
            if (page == maximumPages)
            {
                return audit.IsScanTruncated || audit.Totals.Total > scanned;
            }
        }
        return false;
    }

    private async Task<bool> AddIrisIncidentsAsync(
        List<OperationalIncidentSnapshot> incidents,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var failures = await (
            from request in db.AiRequests.AsNoTracking()
            join execution in db.AiExecutions.AsNoTracking() on request.Id equals execution.AiRequestId
            where request.AccountId == identity.AccountId &&
                  execution.Status != "Completed" &&
                  execution.StartedAtUtc >= fromUtc &&
                  execution.StartedAtUtc <= toUtc
            orderby execution.StartedAtUtc descending
            select new
            {
                request.OperationId,
                execution.Status,
                execution.Provider,
                execution.Model,
                execution.StartedAtUtc
            })
            .Take(SourceScanLimit + 1)
            .ToArrayAsync(cancellationToken);
        incidents.AddRange(failures
            .Take(SourceScanLimit)
            .Select(x => New(
                $"iris:{x.OperationId:N}:{x.Status}", IncidentSource.Iris,
                IncidentSeverity.Critical, "AI_EXECUTION_FAILURE",
                $"Execução da Iris terminou como {x.Status}",
                $"{x.Provider} // {x.Model}", x.OperationId.ToString("N")[..8], x.StartedAtUtc)));
        return failures.Length > SourceScanLimit;
    }

    private async Task<bool> AddFarmJobIncidentsAsync(
        List<OperationalIncidentSnapshot> incidents,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        string[] failureStatuses =
        [
            nameof(FarmJobStatus.Failed),
            nameof(FarmJobStatus.TimedOut),
            nameof(FarmJobStatus.Rejected),
            nameof(FarmJobStatus.Cancelled)
        ];
        var jobs = await db.FarmJobs.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId &&
                failureStatuses.Contains(x.Status) &&
                (x.FinishedAtUtc ?? x.CreatedAtUtc) >= fromUtc &&
                (x.FinishedAtUtc ?? x.CreatedAtUtc) <= toUtc)
            .OrderByDescending(x => x.FinishedAtUtc ?? x.CreatedAtUtc)
            .Select(x => new
            {
                x.Id,
                x.Status,
                x.Workload,
                x.WorkloadVersion,
                x.VerificationPolicy,
                x.FinishedAtUtc,
                x.CreatedAtUtc
            })
            .Take(SourceScanLimit + 1)
            .ToArrayAsync(cancellationToken);
        incidents.AddRange(jobs
            .Take(SourceScanLimit)
            .Select(x =>
            {
                var status = Enum.Parse<FarmJobStatus>(x.Status);
                return New(
                    $"farm:{x.Id:N}:{status}", IncidentSource.Farm,
                    status is FarmJobStatus.Failed or FarmJobStatus.TimedOut
                        ? IncidentSeverity.Critical
                        : IncidentSeverity.Warning,
                    $"FARM_JOB_{status.ToString().ToUpperInvariant()}",
                    $"Job da Farm terminou como {status}",
                    $"{x.Workload} // {x.WorkloadVersion} // {x.VerificationPolicy}",
                    x.Id.ToString("N")[..8], x.FinishedAtUtc ?? x.CreatedAtUtc);
            }));
        return jobs.Length > SourceScanLimit;
    }

    private static OperationalIncidentSnapshot New(
        string key, IncidentSource source, IncidentSeverity severity, string code,
        string title, string detail, string reference, DateTimeOffset detectedAt) =>
        new(key, source, severity, code, title, detail, reference, detectedAt, null);

    private static bool Matches(OperationalIncidentSnapshot incident, string search) =>
        incident.Code.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        incident.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        incident.Detail.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        incident.Reference.Contains(search, StringComparison.OrdinalIgnoreCase);
}
