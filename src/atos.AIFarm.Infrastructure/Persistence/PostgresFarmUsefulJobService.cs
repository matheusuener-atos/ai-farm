using System.Text;
using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

internal sealed class PostgresFarmUsefulJobService(
    PostgresFarmLabService farmLab,
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    FarmManifestSigningOptions signingOptions) : IFarmUsefulJobService
{
    public Task<FarmJobSnapshot> SubmitAsync(
        FarmUsefulJobRequest request,
        CancellationToken cancellationToken = default) =>
        farmLab.RunUsefulWorkloadAsync(request.Validate(), null, null, cancellationToken);

    public Task<FarmJobSnapshot> SubmitAsync(
        FarmUsefulJobRequest request,
        Guid tokenId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (tokenId == Guid.Empty) throw new ArgumentException("Token de API inválido.", nameof(tokenId));
        var key = idempotencyKey?.Trim() ?? string.Empty;
        if (key.Length is < 8 or > 120)
            throw new ArgumentException("X-Idempotency-Key deve possuir entre 8 e 120 caracteres.", nameof(idempotencyKey));
        return farmLab.RunUsefulWorkloadAsync(request.Validate(), tokenId, key, cancellationToken);
    }

    public async Task<IReadOnlyList<FarmJobSnapshot>> ListRecentAsync(
        int count = 20,
        CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 50);
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var jobs = await db.FarmJobs.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId && x.InputVersion != null)
            .OrderByDescending(x => x.CreatedAtUtc).Take(count).ToArrayAsync(cancellationToken);
        var ids = jobs.Select(x => x.Id).ToArray();
        var events = await db.FarmJobEvents.AsNoTracking()
            .Where(x => ids.Contains(x.JobId)).OrderBy(x => x.Sequence).ToArrayAsync(cancellationToken);
        return jobs.Select(x => farmLab.ToSnapshot(x, events.Where(e => e.JobId == x.Id))).ToArray();
    }

    public async Task<FarmUsefulJobOutput?> DownloadOutputAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var job = await db.FarmJobs.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == jobId && x.AccountId == identity.AccountId, cancellationToken);
        if (job?.OutputPayload is null) return null;
        var events = await db.FarmJobEvents.AsNoTracking().Where(x => x.JobId == job.Id)
            .OrderBy(x => x.Sequence).ToArrayAsync(cancellationToken);
        if (!FarmEvidenceSecurity.Verify(job, events, signingOptions, out _).IsValid)
            throw new InvalidOperationException("A saída não pode ser baixada porque sua evidência não é válida.");
        return new FarmUsefulJobOutput(
            $"iris-text-index-{job.Id:N}.json",
            Encoding.UTF8.GetBytes(job.OutputPayload),
            "application/json; charset=utf-8");
    }
}
