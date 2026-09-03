using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresOperationalIncidentService(
    IDbContextFactory<AIFarmDbContext> dbFactory,
    IAIFarmIdentityAccessor identityAccessor,
    IAuditTrail auditTrail,
    IAiUsageQuery usageQuery,
    IFarmLabService farmLab,
    TimeProvider timeProvider) : IOperationalIncidentService
{
    public async Task<IncidentDashboard> SearchAsync(
        IncidentQuery query,
        CancellationToken cancellationToken = default)
    {
        var normalized = query.Normalize();
        var incidents = await DetectAsync(cancellationToken);
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
            hydrated.Count(x => x.IsAcknowledged));
    }

    public async Task<bool> AcknowledgeAsync(
        string incidentKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(incidentKey) || incidentKey.Length > 200) return false;
        var incidents = await DetectAsync(cancellationToken);
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

    private async Task<IReadOnlyList<OperationalIncidentSnapshot>> DetectAsync(
        CancellationToken cancellationToken)
    {
        var audit = await auditTrail.SearchAsync(new AuditQuery(PageSize: 100), cancellationToken);
        var usage = await usageQuery.GetDashboardAsync(50, cancellationToken);
        var farm = await farmLab.GetDashboardAsync(cancellationToken);
        var incidents = new List<OperationalIncidentSnapshot>();

        incidents.AddRange(audit.Events
            .Where(x => x.Integrity == AuditIntegrityStatus.Invalid)
            .Select(x => New(
                $"integrity:{x.Category}:{x.Id:N}", IncidentSource.Integrity,
                IncidentSeverity.Critical, "INTEGRITY_FAILURE",
                $"Falha de integridade em {x.Category}",
                $"{x.Action} // {x.Description}", x.Reference, x.OccurredAtUtc)));
        incidents.AddRange(usage.RecentExecutions
            .Where(x => !x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
            .Select(x => New(
                $"iris:{x.OperationId:N}:{x.Status}", IncidentSource.Iris,
                IncidentSeverity.Critical, "AI_EXECUTION_FAILURE",
                $"Execução da Iris terminou como {x.Status}",
                $"{x.Provider} // {x.Model}", x.OperationId.ToString("N")[..8], x.StartedAtUtc)));
        incidents.AddRange(farm.RecentJobs
            .Where(x => x.Status is FarmJobStatus.Failed or FarmJobStatus.TimedOut or FarmJobStatus.Rejected or FarmJobStatus.Cancelled)
            .Select(x => New(
                $"farm:{x.Id:N}:{x.Status}", IncidentSource.Farm,
                x.Status is FarmJobStatus.Failed or FarmJobStatus.TimedOut ? IncidentSeverity.Critical : IncidentSeverity.Warning,
                $"FARM_JOB_{x.Status.ToString().ToUpperInvariant()}",
                $"Job da Farm terminou como {x.Status}",
                $"{x.Workload} // {x.WorkloadVersion} // {x.VerificationPolicy}",
                x.Id.ToString("N")[..8], x.FinishedAtUtc ?? x.CreatedAtUtc)));
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
        return incidents.GroupBy(x => x.Key).Select(x => x.First()).ToArray();
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
