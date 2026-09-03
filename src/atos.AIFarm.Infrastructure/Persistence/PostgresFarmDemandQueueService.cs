using System.Security.Cryptography;
using System.Text;
using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

internal sealed class PostgresFarmDemandQueueService(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    PostgresFarmLabService farmLab,
    FarmManifestSigningOptions signingOptions,
    TimeProvider timeProvider) : IFarmDemandQueueService, IFarmDemandDispatcher
{
    private const int RecentLimit = 50;
    private const int BatchSize = 20;
    private static readonly string[] DispatchableStatuses =
    [
        FarmDemandStatus.Queued.ToString(), FarmDemandStatus.Processing.ToString(),
        FarmDemandStatus.Submitted.ToString()
    ];

    public async Task<FarmDemandDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await ExpireAsync(timeProvider.GetUtcNow(), identity.AccountId, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var records = await db.FarmDemands.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId)
            .OrderByDescending(x => x.CreatedAtUtc).Take(RecentLimit).ToArrayAsync(cancellationToken);
        return new(await LoadSnapshotsAsync(db, records, cancellationToken));
    }

    public Task<FarmDemandSnapshot> SubmitAsync(FarmDemandRequest request, CancellationToken cancellationToken = default) =>
        SubmitCoreAsync(request, null, null, cancellationToken);

    public Task<FarmDemandSnapshot> SubmitAsync(
        FarmDemandRequest request, Guid tokenId, string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (tokenId == Guid.Empty) throw new ArgumentException("Token de API inválido.", nameof(tokenId));
        return SubmitCoreAsync(request, tokenId, PersonalApiIdempotencyKey.Normalize(idempotencyKey), cancellationToken);
    }

    private async Task<FarmDemandSnapshot> SubmitCoreAsync(
        FarmDemandRequest request, Guid? tokenId, string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validated = request.Validate();
        var parts = FarmTextWorkPartitioner.Partition(
            validated.Submission.InputPayload, validated.TargetShards);
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
                ValidateReplay(replay, validated.Submission, parts.Count);
                return await LoadSnapshotAsync(db, replay, cancellationToken);
            }
        }
        var record = new FarmDemandRecord
        {
            Id = Guid.NewGuid(), AccountId = identity.AccountId,
            ApiTokenId = tokenId, ApiIdempotencyKey = idempotencyKey,
            WorkloadId = validated.Submission.Definition.Id,
            WorkloadVersion = validated.Submission.Definition.Version,
            RequiredLogicalProcessors = validated.Submission.Definition.RequiredLogicalProcessors,
            RequiredMemoryBytes = validated.Submission.Definition.RequiredMemoryBytes,
            TargetShards = parts.Count,
            Label = validated.Submission.Label,
            InputVersion = validated.Submission.InputVersion,
            InputPayload = validated.Submission.InputPayload,
            InputSizeBytes = validated.Submission.InputSizeBytes,
            InputDigest = validated.Submission.InputDigest,
            Priority = (int)validated.Priority,
            Status = FarmDemandStatus.Queued.ToString(), Attempts = 0,
            NextAttemptAtUtc = now, ExpiresAtUtc = now.AddMinutes(validated.LifetimeMinutes),
            Detail = $"Aguardando capacidade para {parts.Count} shard(s).",
            CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.FarmDemands.Add(record);
        db.FarmDemandShards.AddRange(parts.Select((payload, index) => new FarmDemandShardRecord
        {
            Id = Guid.NewGuid(), DemandId = record.Id, Sequence = index + 1,
            InputPayload = payload, InputSizeBytes = Encoding.UTF8.GetByteCount(payload),
            InputDigest = FarmDeterministicWorkloads.ComputeInputDigest(payload)
        }));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (tokenId.HasValue)
        {
            await using var replayDb = await factory.CreateDbContextAsync(cancellationToken);
            var replay = await replayDb.FarmDemands.AsNoTracking().SingleOrDefaultAsync(
                x => x.AccountId == identity.AccountId && x.ApiTokenId == tokenId &&
                     x.ApiIdempotencyKey == idempotencyKey, cancellationToken);
            if (replay is null) throw;
            ValidateReplay(replay, validated.Submission, parts.Count, exception);
            return await LoadSnapshotAsync(replayDb, replay, cancellationToken);
        }
        return await LoadSnapshotAsync(db, record, cancellationToken);
    }

    public async Task<FarmDemandSnapshot> CancelAsync(Guid demandId, CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var changed = await db.FarmDemands
            .Where(x => x.Id == demandId && x.AccountId == identity.AccountId &&
                        x.Status == FarmDemandStatus.Queued.ToString())
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, FarmDemandStatus.Cancelled.ToString())
                .SetProperty(x => x.Detail, "Cancelada pelo usuário antes do primeiro shard.")
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        var record = await db.FarmDemands.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == demandId && x.AccountId == identity.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Demanda não encontrada para esta conta.");
        if (changed == 0)
            throw new InvalidOperationException("Somente demandas ainda sem shards despachados podem ser canceladas.");
        return await LoadSnapshotAsync(db, record, cancellationToken);
    }

    public async Task<FarmUsefulJobOutput?> DownloadOutputAsync(
        Guid demandId, CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var demand = await db.FarmDemands.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == demandId && x.AccountId == identity.AccountId, cancellationToken);
        if (demand?.OutputPayload is null || demand.OutputDigest is null) return null;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(demand.OutputPayload))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(digest), Encoding.ASCII.GetBytes(demand.OutputDigest)))
            throw new InvalidOperationException("O resultado consolidado da demanda não passou na verificação de integridade.");
        var jobs = await db.FarmJobs.AsNoTracking().Where(x => x.DemandId == demand.Id).ToArrayAsync(cancellationToken);
        var jobIds = jobs.Select(x => x.Id).ToArray();
        var events = await db.FarmJobEvents.AsNoTracking().Where(x => jobIds.Contains(x.JobId))
            .OrderBy(x => x.Sequence).ToArrayAsync(cancellationToken);
        if (jobs.Length != demand.TargetShards || jobs.Any(job =>
                !FarmEvidenceSecurity.Verify(job, events.Where(x => x.JobId == job.Id).ToArray(), signingOptions, out _).IsValid))
            throw new InvalidOperationException("Um ou mais shards não possuem evidência válida.");
        return new FarmUsefulJobOutput(
            $"iris-distributed-index-{demand.Id:N}.json",
            Encoding.UTF8.GetBytes(demand.OutputPayload), "application/json; charset=utf-8");
    }

    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await RecoverAsync(now, cancellationToken);
        await ExpireAsync(now, null, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.FarmDemands.AsNoTracking()
            .Where(x => DispatchableStatuses.Contains(x.Status) && x.NextAttemptAtUtc <= now &&
                        (x.Status != FarmDemandStatus.Queued.ToString() || x.ExpiresAtUtc > now))
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
            .Where(x => x.Id == id && DispatchableStatuses.Contains(x.Status) &&
                        x.NextAttemptAtUtc <= now &&
                        (x.Status != FarmDemandStatus.Queued.ToString() || x.ExpiresAtUtc > now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, FarmDemandStatus.Dispatching.ToString())
                .SetProperty(x => x.ClaimedAtUtc, now)
                .SetProperty(x => x.Attempts, x => x.Attempts + 1)
                .SetProperty(x => x.Detail, "Coordenador distribuído processando os shards.")
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        return changed == 0 ? null : await db.FarmDemands.AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
    }

    private async Task DispatchAsync(FarmDemandRecord demand, CancellationToken cancellationToken)
    {
        try
        {
            var definition = FarmWorkloadCatalog.GetRequired(demand.WorkloadId);
            if (!ContractMatches(demand, definition))
            {
                await FinishFailureAsync(demand.Id,
                    "O contrato da demanda não corresponde mais ao catálogo ativo.", CancellationToken.None);
                return;
            }
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var shards = await db.FarmDemandShards.Where(x => x.DemandId == demand.Id)
                .OrderBy(x => x.Sequence).ToArrayAsync(cancellationToken);
            var jobs = await db.FarmJobs.AsNoTracking().Where(x => x.DemandId == demand.Id)
                .ToArrayAsync(cancellationToken);
            foreach (var shard in shards.Where(x => x.JobId is null))
            {
                var recovered = jobs.SingleOrDefault(x => x.DemandShardIndex == shard.Sequence);
                if (recovered is not null) shard.JobId = recovered.Id;
            }
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);
            jobs = await db.FarmJobs.AsNoTracking().Where(x => x.DemandId == demand.Id).ToArrayAsync(cancellationToken);
            if (jobs.Length == demand.TargetShards)
            {
                await ReconcileAsync(demand, shards, jobs, cancellationToken);
                return;
            }

            foreach (var shard in shards.Where(x => x.JobId is null))
            {
                var submission = new FarmUsefulJobSubmission(
                    definition, ShardLabel(demand.Label, shard.Sequence, demand.TargetShards),
                    demand.InputVersion, shard.InputPayload, shard.InputSizeBytes, shard.InputDigest);
                var job = await farmLab.RunQueuedDemandAsync(
                    demand.AccountId, demand.Id, shard.Sequence, demand.TargetShards,
                    submission, cancellationToken);
                await LinkShardAsync(demand.Id, shard.Id, job.Id, cancellationToken);
                shard.JobId = job.Id;
            }
            await SetStateAsync(demand.Id, FarmDemandStatus.Submitted, timeProvider.GetUtcNow().AddSeconds(5),
                $"{demand.TargetShards}/{demand.TargetShards} shards despachados; aguardando evidências.", cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await DeferAsync(demand, exception.Message, CancellationToken.None);
        }
        catch (Exception exception)
        {
            await FinishFailureAsync(demand.Id, Limit(exception.Message), CancellationToken.None);
        }
    }

    private async Task ReconcileAsync(
        FarmDemandRecord demand, FarmDemandShardRecord[] shards, FarmJobRecord[] jobs,
        CancellationToken cancellationToken)
    {
        if (jobs.Any(x => FarmJobLifecycle.ActiveStatuses.Contains(x.Status)))
        {
            await SetStateAsync(demand.Id, FarmDemandStatus.Submitted, timeProvider.GetUtcNow().AddSeconds(5),
                $"{jobs.Count(x => x.Status == FarmJobStatus.Completed.ToString())}/{demand.TargetShards} shards concluídos.", cancellationToken);
            return;
        }
        var failed = jobs.Where(x => x.Status != FarmJobStatus.Completed.ToString()).ToArray();
        if (failed.Length > 0)
        {
            await FinishFailureAsync(demand.Id,
                $"{failed.Length} shard(s) terminaram sem evidência aprovada.", cancellationToken);
            return;
        }
        await using (var verificationDb = await factory.CreateDbContextAsync(cancellationToken))
        {
            var jobIds = jobs.Select(x => x.Id).ToArray();
            var events = await verificationDb.FarmJobEvents.AsNoTracking()
                .Where(x => jobIds.Contains(x.JobId)).OrderBy(x => x.Sequence).ToArrayAsync(cancellationToken);
            if (jobs.Any(job => job.OutputPayload is null || job.OutputDigest is null ||
                    !FarmEvidenceSecurity.Verify(
                        job, events.Where(x => x.JobId == job.Id).ToArray(), signingOptions, out _).IsValid))
            {
                await FinishFailureAsync(demand.Id,
                    "A consolidação recusou um shard sem saída ou evidência válida.", cancellationToken);
                return;
            }
        }
        var bySequence = jobs.ToDictionary(x => x.DemandShardIndex!.Value);
        var aggregate = FarmDistributedTextIndex.Aggregate(demand.InputDigest,
            shards.Select(shard =>
            {
                var job = bySequence[shard.Sequence];
                return (shard.Sequence, shard.InputDigest, job.OutputDigest!, job.OutputPayload!);
            }).ToArray());
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.FarmDemands.Where(x => x.Id == demand.Id && x.Status == FarmDemandStatus.Dispatching.ToString())
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, FarmDemandStatus.Completed.ToString())
                .SetProperty(x => x.OutputVersion, FarmDistributedTextIndex.Version)
                .SetProperty(x => x.OutputPayload, aggregate.Payload)
                .SetProperty(x => x.OutputSizeBytes, Encoding.UTF8.GetByteCount(aggregate.Payload))
                .SetProperty(x => x.OutputDigest, aggregate.Digest)
                .SetProperty(x => x.FinishedAtUtc, timeProvider.GetUtcNow())
                .SetProperty(x => x.ClaimedAtUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.Detail, $"{demand.TargetShards} shards verificados e consolidados.")
                .SetProperty(x => x.UpdatedAtUtc, timeProvider.GetUtcNow()), cancellationToken);
    }

    private async Task LinkShardAsync(Guid demandId, Guid shardId, Guid jobId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.FarmDemandShards.Where(x => x.Id == shardId && x.DemandId == demandId && x.JobId == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.JobId, jobId), cancellationToken);
        await db.FarmDemands.Where(x => x.Id == demandId && x.JobId == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.JobId, jobId), cancellationToken);
    }

    private async Task DeferAsync(FarmDemandRecord demand, string detail, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var dispatched = await db.FarmDemandShards.CountAsync(x => x.DemandId == demand.Id && x.JobId != null, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (dispatched == 0 && now >= demand.ExpiresAtUtc)
        {
            await SetStateAsync(demand.Id, FarmDemandStatus.Expired, now,
                "A demanda expirou antes de despachar o primeiro shard.", cancellationToken);
            return;
        }
        var seconds = Math.Min(300, 5 * (1 << Math.Min(demand.Attempts - 1, 6)));
        await SetStateAsync(demand.Id,
            dispatched == 0 ? FarmDemandStatus.Queued : FarmDemandStatus.Processing,
            now.AddSeconds(seconds), Limit(detail), cancellationToken);
    }

    private async Task SetStateAsync(
        Guid id, FarmDemandStatus status, DateTimeOffset nextAttempt, string detail,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.FarmDemands.Where(x => x.Id == id && x.Status == FarmDemandStatus.Dispatching.ToString())
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, status.ToString())
                .SetProperty(x => x.NextAttemptAtUtc, nextAttempt)
                .SetProperty(x => x.ClaimedAtUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.Detail, detail)
                .SetProperty(x => x.UpdatedAtUtc, timeProvider.GetUtcNow()), cancellationToken);
    }

    private async Task FinishFailureAsync(Guid id, string detail, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.FarmDemands.Where(x => x.Id == id && x.Status == FarmDemandStatus.Dispatching.ToString())
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, FarmDemandStatus.Failed.ToString())
                .SetProperty(x => x.FinishedAtUtc, timeProvider.GetUtcNow())
                .SetProperty(x => x.ClaimedAtUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.Detail, detail)
                .SetProperty(x => x.UpdatedAtUtc, timeProvider.GetUtcNow()), cancellationToken);
    }

    private async Task RecoverAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var abandoned = db.FarmDemands.Where(x =>
            x.Status == FarmDemandStatus.Dispatching.ToString() && x.ClaimedAtUtc < now.AddMinutes(-2));
        await abandoned.Where(x => db.FarmJobs.Any(j => j.DemandId == x.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, FarmDemandStatus.Processing.ToString())
                .SetProperty(x => x.NextAttemptAtUtc, now)
                .SetProperty(x => x.ClaimedAtUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.Detail, "Fan-out interrompido; retomado a partir dos shards persistidos.")
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        await abandoned.Where(x => !db.FarmJobs.Any(j => j.DemandId == x.Id))
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
        await db.FarmDemands.Where(x => (!accountId.HasValue || x.AccountId == accountId) &&
                x.Status == FarmDemandStatus.Queued.ToString() && x.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, FarmDemandStatus.Expired.ToString())
                .SetProperty(x => x.Detail, "A demanda expirou antes de despachar o primeiro shard.")
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
    }

    private async Task<FarmDemandSnapshot[]> LoadSnapshotsAsync(
        AIFarmDbContext db, FarmDemandRecord[] records, CancellationToken cancellationToken)
    {
        var ids = records.Select(x => x.Id).ToArray();
        var shards = await db.FarmDemandShards.AsNoTracking().Where(x => ids.Contains(x.DemandId))
            .OrderBy(x => x.Sequence).ToArrayAsync(cancellationToken);
        var jobs = await db.FarmJobs.AsNoTracking().Where(x => x.DemandId != null && ids.Contains(x.DemandId.Value))
            .ToArrayAsync(cancellationToken);
        return records.Select(record => ToSnapshot(record,
            shards.Where(x => x.DemandId == record.Id), jobs.Where(x => x.DemandId == record.Id))).ToArray();
    }

    private async Task<FarmDemandSnapshot> LoadSnapshotAsync(
        AIFarmDbContext db, FarmDemandRecord record, CancellationToken cancellationToken)
    {
        var shards = await db.FarmDemandShards.AsNoTracking().Where(x => x.DemandId == record.Id)
            .OrderBy(x => x.Sequence).ToArrayAsync(cancellationToken);
        var jobs = await db.FarmJobs.AsNoTracking().Where(x => x.DemandId == record.Id).ToArrayAsync(cancellationToken);
        return ToSnapshot(record, shards, jobs);
    }

    private static FarmDemandSnapshot ToSnapshot(
        FarmDemandRecord record, IEnumerable<FarmDemandShardRecord> shardRecords,
        IEnumerable<FarmJobRecord> jobRecords)
    {
        var definition = FarmWorkloadCatalog.GetRequired(record.WorkloadId);
        var jobs = jobRecords.ToDictionary(x => x.DemandShardIndex ?? 0);
        var shards = shardRecords.Select(shard =>
        {
            jobs.TryGetValue(shard.Sequence, out var job);
            return new FarmDemandShardSnapshot(
                shard.Sequence, shard.InputSizeBytes, shard.InputDigest, shard.JobId,
                job is null ? null : Enum.Parse<FarmJobStatus>(job.Status), job?.OutputDigest);
        }).ToArray();
        var primary = shards.FirstOrDefault(x => x.JobId.HasValue);
        return new(record.Id, record.WorkloadId, definition.Workload, record.WorkloadVersion,
            record.RequiredLogicalProcessors, record.RequiredMemoryBytes, record.TargetShards,
            shards.Count(x => x.JobId.HasValue), shards.Count(x => x.JobStatus == FarmJobStatus.Completed),
            shards.Count(x => x.JobStatus is FarmJobStatus.Failed or FarmJobStatus.Cancelled or
                FarmJobStatus.TimedOut or FarmJobStatus.Rejected),
            record.Label, record.InputSizeBytes, record.InputDigest,
            (FarmDemandPriority)record.Priority, Enum.Parse<FarmDemandStatus>(record.Status),
            record.Attempts, record.NextAttemptAtUtc, record.ExpiresAtUtc, primary?.JobId,
            primary?.JobStatus, shards, record.OutputDigest, record.Detail,
            record.CreatedAtUtc, record.UpdatedAtUtc);
    }

    private static void ValidateReplay(
        FarmDemandRecord replay, FarmUsefulJobSubmission submission, int targetShards,
        Exception? inner = null)
    {
        if (replay.InputDigest != submission.InputDigest || replay.WorkloadId != submission.Definition.Id ||
            replay.TargetShards != targetShards)
            throw new InvalidOperationException("A chave de idempotência já foi usada com outra demanda.", inner);
    }

    private static bool ContractMatches(FarmDemandRecord demand, FarmWorkloadDefinition definition) =>
        definition.Version == demand.WorkloadVersion &&
        definition.RequiredLogicalProcessors == demand.RequiredLogicalProcessors &&
        definition.RequiredMemoryBytes == demand.RequiredMemoryBytes;

    private static string Limit(string value) => value.Length <= 500 ? value : value[..500];

    private static string ShardLabel(string label, int sequence, int total)
    {
        var suffix = $" // shard {sequence}/{total}";
        return label.Length + suffix.Length <= 120
            ? label + suffix
            : label[..(120 - suffix.Length)] + suffix;
    }
}
