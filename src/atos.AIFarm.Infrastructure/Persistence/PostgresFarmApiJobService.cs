using atos.AIFarm.Core.Application;

namespace atos.AIFarm.Infrastructure.Persistence;

internal sealed class PostgresFarmApiJobService(PostgresFarmLabService farmLab)
    : IFarmApiJobService
{
    public Task<FarmJobSnapshot> SubmitAsync(
        FarmApiJobRequest request,
        Guid tokenId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var definition = request.Validate();
        if (tokenId == Guid.Empty)
            throw new ArgumentException("O identificador do token é inválido.", nameof(tokenId));
        var normalizedKey = PersonalApiIdempotencyKey.Normalize(idempotencyKey);
        return farmLab.RunApiWorkloadAsync(
            definition.Id,
            tokenId,
            normalizedKey,
            cancellationToken);
    }

    public Task<FarmJobSnapshot?> GetAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        farmLab.GetJobAsync(jobId, cancellationToken);

    public async Task<FarmJobSnapshot> CancelAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await farmLab.CancelJobAsync(jobId, cancellationToken);
        return await farmLab.GetJobAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException("Job não encontrado para esta conta.");
    }

    public Task<FarmJobEvidenceDownload?> DownloadEvidenceAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        farmLab.DownloadEvidenceAsync(jobId, cancellationToken);
}
