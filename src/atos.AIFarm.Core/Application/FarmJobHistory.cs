namespace atos.AIFarm.Core.Application;

public enum FarmJobOrigin
{
    Interactive,
    PersonalApi,
    Scheduled
}

public sealed record FarmJobHistoryQuery(
    string? Search = null,
    string? WorkloadId = null,
    FarmJobStatus? Status = null,
    FarmJobOrigin? Origin = null,
    bool IntegrityFailuresOnly = false,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int Page = 1,
    int PageSize = 20)
{
    public FarmJobHistoryQuery Normalize()
    {
        if (Page < 1) throw new ArgumentOutOfRangeException(nameof(Page));
        if (PageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(PageSize));
        if (FromUtc is not null && ToUtc is not null && FromUtc > ToUtc)
            throw new ArgumentException("O início do período não pode ser posterior ao fim.");
        var search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        if (search?.Length > 120)
            throw new ArgumentException("A busca deve ter no máximo 120 caracteres.", nameof(Search));
        var workloadId = string.IsNullOrWhiteSpace(WorkloadId) ? null : WorkloadId.Trim();
        if (workloadId is not null)
            FarmWorkloadCatalog.GetRequired(workloadId);
        return this with
        {
            Search = search,
            WorkloadId = workloadId,
            FromUtc = FromUtc?.ToUniversalTime(),
            ToUtc = ToUtc?.ToUniversalTime()
        };
    }
}

public sealed record FarmJobHistoryItem(
    FarmJobSnapshot Job,
    FarmJobOrigin Origin);

public sealed record FarmJobHistoryTotals(
    int Total,
    int Active,
    int Completed,
    int Failed,
    int IntegrityFailures,
    long VerifiedProcessingPointsMilli);

public sealed record FarmJobHistoryPage(
    IReadOnlyList<FarmJobHistoryItem> Items,
    FarmJobHistoryTotals Totals,
    int Page,
    int PageSize,
    bool IsScanTruncated)
{
    public int TotalPages => Math.Max(1, (Totals.Total + PageSize - 1) / PageSize);
    public bool HasPreviousPage => Page > 1;
    public bool HasNextPage => Page < TotalPages;
}

public sealed record FarmJobHistoryExport(
    string FileName,
    string ContentType,
    byte[] Content);

public interface IFarmJobHistoryService
{
    Task<FarmJobHistoryPage> SearchAsync(
        FarmJobHistoryQuery query,
        CancellationToken cancellationToken = default);

    Task<FarmJobHistoryExport> ExportAsync(
        FarmJobHistoryQuery query,
        CancellationToken cancellationToken = default);
}
