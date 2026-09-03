namespace atos.AIFarm.Core.Application;

public sealed record FarmApiJobRequest(string WorkloadId)
{
    public FarmWorkloadDefinition Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkloadId))
            throw new ArgumentException("Informe o workloadId.", nameof(WorkloadId));
        return FarmWorkloadCatalog.GetRequired(WorkloadId);
    }
}

public interface IFarmApiJobService
{
    Task<FarmJobSnapshot> SubmitAsync(
        FarmApiJobRequest request,
        Guid tokenId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
    Task<FarmJobSnapshot?> GetAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);
    Task<FarmJobSnapshot> CancelAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);
    Task<FarmJobEvidenceDownload?> DownloadEvidenceAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);
}

public static class PersonalApiIdempotencyKey
{
    public static string Normalize(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 8 or > 120 || normalized.Any(char.IsControl))
            throw new ArgumentException(
                "X-Idempotency-Key deve conter entre 8 e 120 caracteres válidos.",
                nameof(value));
        return normalized;
    }
}
