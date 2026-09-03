namespace atos.AIFarm.Core.Application;

public enum AuditCategory
{
    Ledger,
    Iris,
    Farm,
    Economics,
    Reward,
    Security
}

public enum AuditIntegrityStatus
{
    NotApplicable,
    Valid,
    Invalid
}

public sealed record AuditQuery(
    string? Search = null,
    AuditCategory? Category = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int Page = 1,
    int PageSize = 25)
{
    public AuditQuery Normalize()
    {
        if (Page < 1) throw new ArgumentOutOfRangeException(nameof(Page));
        if (PageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(PageSize));
        if (FromUtc is not null && ToUtc is not null && FromUtc > ToUtc)
            throw new ArgumentException("O início do período não pode ser posterior ao fim.");
        var search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        if (search?.Length > 120)
            throw new ArgumentException("A busca deve ter no máximo 120 caracteres.", nameof(Search));
        return this with
        {
            Search = search,
            FromUtc = FromUtc?.ToUniversalTime(),
            ToUtc = ToUtc?.ToUniversalTime()
        };
    }
}

public sealed record AuditEventSnapshot(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    AuditCategory Category,
    string Action,
    string Status,
    string Description,
    string Reference,
    string? Correlation,
    long? AmountMinor,
    string? Digest,
    AuditIntegrityStatus Integrity);

public sealed record AuditTotals(
    int Total,
    int Ledger,
    int Iris,
    int Farm,
    int Economics,
    int Reward,
    int Security,
    int IntegrityFailures);

public sealed record AuditPage(
    IReadOnlyList<AuditEventSnapshot> Events,
    AuditTotals Totals,
    int Page,
    int PageSize,
    bool IsScanTruncated)
{
    public int TotalPages => Math.Max(1, (Totals.Total + PageSize - 1) / PageSize);
    public bool HasPreviousPage => Page > 1;
    public bool HasNextPage => Page < TotalPages;
}

public sealed record AuditExport(string FileName, string ContentType, byte[] Content);

public interface IAuditTrail
{
    Task<AuditPage> SearchAsync(AuditQuery query, CancellationToken cancellationToken = default);
    Task<AuditExport> ExportAsync(AuditQuery query, CancellationToken cancellationToken = default);
}
