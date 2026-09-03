namespace atos.AIFarm.Core.Application;

public enum IncidentSeverity { Warning, Critical }
public enum IncidentSource { Integrity, Iris, Farm, Node }

public sealed record IncidentQuery(
    string? Search = null,
    IncidentSeverity? Severity = null,
    bool OpenOnly = false)
{
    public IncidentQuery Normalize()
    {
        var search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        if (search?.Length > 120)
            throw new ArgumentException("A busca deve ter no máximo 120 caracteres.", nameof(Search));
        return this with { Search = search };
    }
}

public sealed record OperationalIncidentSnapshot(
    string Key,
    IncidentSource Source,
    IncidentSeverity Severity,
    string Code,
    string Title,
    string Detail,
    string Reference,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset? AcknowledgedAtUtc)
{
    public bool IsAcknowledged => AcknowledgedAtUtc is not null;
}

public sealed record IncidentDashboard(
    IReadOnlyList<OperationalIncidentSnapshot> Incidents,
    int Total,
    int Open,
    int Critical,
    int Warning,
    int Acknowledged);

public interface IOperationalIncidentService
{
    Task<IncidentDashboard> SearchAsync(IncidentQuery query, CancellationToken cancellationToken = default);
    Task<bool> AcknowledgeAsync(string incidentKey, CancellationToken cancellationToken = default);
}
