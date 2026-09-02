using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

internal sealed class PostgresFarmDemandQueueService(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    PostgresFarmLabService farmLab,
    TimeProvider timeProvider) : IFarmDemandQueueService, IFarmDemandDispatcher
{
    private const int RecentLimit = 50;
    private const int BatchSize = 20;

    public async Task<FarmDemandDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await ExpireAsync(timeProvider.GetUtcNow(), identity.AccountId, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var demands = await db.FarmDemands.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(RecentLimit)
            .ToArrayAsync(cancellationToken);
        var jobIds = demands.Where(x => x.JobId.HasValue).Select(x => x.JobId!.Value).ToArray();
        var jobs = await db.FarmJobs.AsNoTracking()
            .Where(x => jobIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        return new(demands.Select(x => ToSnapshot(
            x, x.JobId is Guid id && jobs.TryGetValue(id, out var job) ? job : null)).ToArray());
    }

    public Task<FarmDemandSnapshot> SubmitAsync(
        FarmDemandRequest request,
        CancellationToken cancellationToken = default) =>
        SubmitCoreAsync(request, null, null, cancellationToken);

    public Task<FarmDemandSnapshot> SubmitAsync(
        FarmDemandRequest request,
        Guid tokenId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (tokenId == Guid.Empty) throw new ArgumentException("Token de API inválido.", nameof(tokenId));
        return SubmitCoreAsync(request, tokenId,
            PersonalApiIdempotencyKey.Normalize(idempotencyKey), cancellationToken);
    }

    private async Task<FarmDemandSnapshot> SubmitCoreAsync(
        FarmDemandRequest request,
        Guid? tokenId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validated = request.Validate();
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (tokenId is Guid apiTokenId)
        {
            var replay = await db.FarmDemands.AsNoTracking().SingleOrDefaultAsync(
                x => x.AccountId == identity.AccountId && x.ApiTokenId == apiTokenId &&
                     x.ApiIdempotencyKey == idempotencyKey, cancellationToken);
            if (replay is not null)
            {
                if (replay.InputDigest != validated.Submission.InputDigest ||
                    replay.WorkloadId != validated.Submission.Definition.Id)
                    throw new InvalidOperationException("A chave de idempotência já foi usada com outra demanda.");
                return ToSnapshot(replay, null);
            }
        }
        var record = new FarmDemandRecord
        {
            Id = Guid.NewGuid(), AccountId = identity.AccountId,
            ApiTokenId = tokenId, ApiIdempotencyKey = idempotencyKey,
            WorkloadId = validated.Submission.Definition.Id,
            Label = validated.Submission.Label,
            InputVersion = validated.Submission.InputVersion,
            InputPayload = validated.Submission.InputPayload,
            InputSizeBytes = validated.Submission.InputSizeBytes,
            InputDigest = validated.Submission.InputDigest,
            Priority = (int)validated.Priority,
            Status = FarmDemandStatus.Queued.ToString(), Attempts = 0,
            NextAttemptAtUtc = now, ExpiresAtUtc = now.AddMinutes(validated.LifetimeMinutes),
            Detail = "Aguardando capacidade compatível no pool.",
            CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.FarmDemands.Add(record);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (tokenId.HasValue)
        {
            await using var replayDb = await factory.CreateDbContextAsync(cancellationToken);
            var replay = await replayDb.FarmDemands.AsNoTracking().SingleOrDefaultAsync(
                x => x.AccountId == identity.AccountId && x.ApiTokenId == tokenId &&
                     x.ApiIdempotencyKey == idempotencyKey, cancellationToken);
            if (replay is null) throw;
            if (replay.InputDigest != record.InputDigest || replay.WorkloadId != record.WorkloadId)
                throw new InvalidOperationException("A chave de idempotência já foi usada com outra demanda.", exception);
            return ToSnapshot(replay, null);
        }
        return ToSnapshot(record, null);
    }

    public async Task<FarmDemandSnapshot> CancelAsync(
        Guid demandId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var changed = await db.FarmDemands
            .Where(x => x.Id == demandId && x.AccountId == identity.AccountId &&
                        x.Status == FarmDemandStatus.Queued.ToString())
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, FarmDemandStatus.Cancelled.ToString())
                .SetProperty(x => x.Detail, "Cancelada pelo usuário antes do despacho.")
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        var record = await db.FarmDemands.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == demandId && x.AccountId == identity.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Demanda não encontrada para esta conta.");
        if (changed == 0)
            throw new InvalidOperationException("Somente demandas aguardando na fila podem ser canceladas.");
        return ToSnapshot(record, null);
    }

    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await RecoverAsync(now, cancellationToken);
        await ExpireAsync(now, null, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.FarmDemands.AsNoTracking()
            .Where(x => x.Status == FarmDemandStatus.Queued.ToString() &&
                        x.NextAttemptAtUtc <= now && x.ExpiresAtUtc > now)
            .OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAtUtc)
            .Take(BatchSize).Select(x => x.Id).ToArrayAsync(cancellationToken);
        var processed = 0;
        foreach (var id in candidates)
        {
            var claim = await TryClaimAsync(id, now, cancellationToken);
            if (claim is null) continue;
            processed++;
            await DispatchAsync(claim, cancellationToken);
        }
        return processed;
    }

    private async Task<FarmDemandRecord?> TryClaimAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var changed = await db.FarmDemands
            .Where(x => x.Id == id && x.Status == FarmDemandStatus.Queued.ToString() &&
                        x.NextAttemptAtUtc <= now && x.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, FarmDemandStatus.Dispatching.ToString())
                .SetProperty(x => x.ClaimedAtUtc, now)
                .SetProperty(x => x.Attempts, x => x.Attempts + 1)
                .SetProperty(x => x.Detail, "Capacidade localizada; iniciando despacho.")
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        return changed == 0 ? null : await db.FarmDemands.AsNoTracking()
            .SingleAsync(x => x.Id == id, cancellationToken);
    }

    private async Task DispatchAsync(FarmDemandRecord demand, CancellationToken cancellationToken)
    {
        try
        {
            var definition = FarmWorkloadCatalog.GetRequired(demand.WorkloadId);
            var submission = new FarmUsefulJobSubmission(
                definition, demand.Label, demand.InputVersion, demand.InputPayload,
                demand.InputSizeBytes, demand.InputDigest);
            var job = await farmLab.RunQueuedDemandAsync(
                demand.AccountId, demand.Id, submission, cancellationToken);
            await FinishAsync(demand.Id, FarmDemandStatus.Submitted, job.Id,
                $"Job {job.Id:N} criado e vinculado à demanda.", CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            var now = timeProvider.GetUtcNow();
            if (now >= demand.ExpiresAtUtc)
                await FinishAsync(demand.Id, FarmDemandStatus.Expired, null,
                    "A demanda expirou antes de obter capacidade.", CancellationToken.None);
            else
            {
                var seconds = Math.Min(300, 5 * (1 << Math.Min(demand.Attempts - 1, 6)));
                await RequeueAsync(demand.Id, now.AddSeconds(seconds), Limit(exception.Message), CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            await FinishAsync(demand.Id, FarmDemandStatus.Failed, null, Limit(exception.Message), CancellationToken.None);
        }
    }

    private async Task RequeueAsync(Guid id, DateTimeOffset nextAttempt, string detail, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.FarmDemands.Where(x => x.Id == id && x.Status == FarmDemandStatus.Dispatching.ToString())
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, FarmDemandStatus.Queued.ToString())
                .SetProperty(x => x.NextAttemptAtUtc, nextAttempt)
                .SetProperty(x => x.ClaimedAtUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.Detail, detail)
                .SetProperty(x => x.UpdatedAtUtc, timeProvider.GetUtcNow()), cancellationToken);
    }

    private async Task FinishAsync(Guid id, FarmDemandStatus status, Guid? jobId, string detail, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.FarmDemands.Where(x => x.Id == id && x.Status == FarmDemandStatus.Dispatching.ToString())
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, status.ToString())
                .SetProperty(x => x.JobId, jobId)
                .SetProperty(x => x.Detail, detail)
                .SetProperty(x => x.UpdatedAtUtc, timeProvider.GetUtcNow()), cancellationToken);
    }

    private async Task RecoverAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.FarmDemands
            .Where(x => x.Status == FarmDemandStatus.Dispatching.ToString() &&
                        x.ClaimedAtUtc < now.AddMinutes(-2))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, FarmDemandStatus.Queued.ToString())
                .SetProperty(x => x.NextAttemptAtUtc, now)
                .SetProperty(x => x.ClaimedAtUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.Detail, "Despacho interrompido; retomado automaticamente.")
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
    }

    private async Task ExpireAsync(DateTimeOffset now, Guid? accountId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.FarmDemands
            .Where(x => (!accountId.HasValue || x.AccountId == accountId) &&
                        x.Status == FarmDemandStatus.Queued.ToString() && x.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, FarmDemandStatus.Expired.ToString())
                .SetProperty(x => x.Detail, "A demanda expirou antes de obter capacidade.")
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
    }

    private static FarmDemandSnapshot ToSnapshot(FarmDemandRecord record, FarmJobRecord? job)
    {
        var definition = FarmWorkloadCatalog.GetRequired(record.WorkloadId);
        return new(record.Id, record.WorkloadId, definition.Workload, definition.Version,
            record.Label, record.InputSizeBytes, record.InputDigest,
            (FarmDemandPriority)record.Priority, Enum.Parse<FarmDemandStatus>(record.Status),
            record.Attempts, record.NextAttemptAtUtc, record.ExpiresAtUtc, record.JobId,
            job is null ? null : Enum.Parse<FarmJobStatus>(job.Status), record.Detail,
            record.CreatedAtUtc, record.UpdatedAtUtc);
    }

    private static string Limit(string value) => value.Length <= 500 ? value : value[..500];
}
