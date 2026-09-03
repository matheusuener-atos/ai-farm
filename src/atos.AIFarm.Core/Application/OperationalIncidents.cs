namespace atos.AIFarm.Core.Application;

public enum IncidentSeverity { Warning, Critical }
public enum IncidentSource { Integrity, Iris, Farm, Node }

public sealed record IncidentQuery(
    string? Search = null,
    IncidentSeverity? Severity = null,
    bool OpenOnly = false,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null)
{
    public const int DefaultWindowDays = 7;
    public const int MaximumWindowDays = 30;

    public IncidentQuery Normalize()
    {
        var search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        if (search?.Length > 120)
            throw new ArgumentException("A busca deve ter no máximo 120 caracteres.", nameof(Search));
        if (FromUtc is not null && ToUtc is not null && FromUtc > ToUtc)
            throw new ArgumentException("O início da janela deve anteceder o fim.", nameof(FromUtc));
        return this with
        {
            Search = search,
            FromUtc = FromUtc?.ToUniversalTime(),
            ToUtc = ToUtc?.ToUniversalTime()
        };
    }

    /// <summary>
    /// Resolve a janela efetiva: padrão de 7 dias terminando agora, com o
    /// intervalo limitado a <see cref="MaximumWindowDays"/> dias.
    /// </summary>
    public (DateTimeOffset FromUtc, DateTimeOffset ToUtc) ResolveWindow(DateTimeOffset nowUtc)
    {
        var to = (ToUtc ?? nowUtc).ToUniversalTime();
        var from = (FromUtc ?? to.AddDays(-DefaultWindowDays)).ToUniversalTime();
        if (from > to)
            throw new ArgumentException("O início da janela deve anteceder o fim.", nameof(FromUtc));
        if (to - from > TimeSpan.FromDays(MaximumWindowDays))
            throw new ArgumentException(
                $"A janela de incidentes é limitada a {MaximumWindowDays} dias.", nameof(FromUtc));
        return (from, to);
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
    int Acknowledged,
    DateTimeOffset FromUtc = default,
    DateTimeOffset ToUtc = default,
    bool IsTruncated = false);

public interface IOperationalIncidentService
{
    Task<IncidentDashboard> SearchAsync(IncidentQuery query, CancellationToken cancellationToken = default);
    Task<bool> AcknowledgeAsync(string incidentKey, CancellationToken cancellationToken = default);
}
