using System.Diagnostics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

/// <summary>
/// Ponte da farm contínua: o serviço do Agent pede um job de consumo para um
/// node específico sem depender da identidade de usuário do IFarmLabService.
/// </summary>
internal interface IFarmContinuousDispatcher
{
    Task<FarmJobSnapshot> RunContinuousWorkloadAsync(
        Guid accountId,
        Guid nodeId,
        CancellationToken cancellationToken = default);
}

internal sealed class PostgresFarmLabService(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    TimeProvider timeProvider,
    FarmManifestSigningOptions signingOptions,
    FarmNodeReliabilityPolicy nodeReliabilityPolicy,
    FarmRewardAutoProposer rewardAutoProposer) : IFarmLabService, IFarmContinuousDispatcher
{
    private const string ManifestVersion = FarmManifestSecurity.EmbeddedVersion;
    internal const string RemoteManifestVersion = FarmManifestSecurity.AgentProfileVersion;
    private static readonly TimeSpan AgentHeartbeatTolerance = TimeSpan.FromSeconds(90);

    public async Task<FarmDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var embedded = await EnsureEmbeddedNodeAsync(db, identity.AccountId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        await FarmJobLifecycle.ExpireAsync(
            db,
            now,
            cancellationToken,
            accountId: identity.AccountId);
        embedded.LastSeenAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return await BuildDashboardAsync(db, identity.AccountId, cancellationToken);
    }

    public async Task<FarmDashboard> SetNodeStatusAsync(
        FarmNodeStatus status,
        CancellationToken cancellationToken = default)
    {
        if (status is not (FarmNodeStatus.Online or FarmNodeStatus.Paused))
            throw new ArgumentOutOfRangeException(nameof(status));

        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await EnsureEmbeddedNodeAsync(db, identity.AccountId, cancellationToken);
        var node = await GetPrimaryNodeAsync(db, identity.AccountId, cancellationToken);
        return await SetNodeStatusCoreAsync(db, identity.AccountId, node, status, cancellationToken);
    }

    public async Task<FarmDashboard> SetNodeStatusAsync(
        Guid nodeId,
        FarmNodeStatus status,
        CancellationToken cancellationToken = default)
    {
        if (status is not (FarmNodeStatus.Online or FarmNodeStatus.Paused))
            throw new ArgumentOutOfRangeException(nameof(status));
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await db.FarmNodes.SingleOrDefaultAsync(
            x => x.Id == nodeId && x.AccountId == identity.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Node não encontrado para esta conta.");
        return await SetNodeStatusCoreAsync(db, identity.AccountId, node, status, cancellationToken);
    }

    public async Task<FarmDashboard> SetNodeSharingAsync(
        Guid nodeId,
        bool sharePlatformWork,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await db.FarmNodes.SingleOrDefaultAsync(
            x => x.Id == nodeId && x.AccountId == identity.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Node não encontrado para esta conta.");
        if (node.ConnectionKind != FarmNodeConnectionKind.Agent.ToString())
            throw new InvalidOperationException(
                "O compartilhamento com a plataforma está disponível apenas para Compute Agents.");
        node.SharePlatformWork = sharePlatformWork;
        await db.SaveChangesAsync(cancellationToken);
        return await BuildDashboardAsync(db, identity.AccountId, cancellationToken);
    }

    private async Task<FarmDashboard> SetNodeStatusCoreAsync(
        AIFarmDbContext db,
        Guid accountId,
        FarmNodeRecord node,
        FarmNodeStatus status,
        CancellationToken cancellationToken)
    {
        node.Status = status.ToString();
        if (node.ConnectionKind == FarmNodeConnectionKind.Embedded.ToString())
            node.LastSeenAtUtc = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return await BuildDashboardAsync(db, accountId, cancellationToken);
    }

    public async Task<FarmDashboard> ConfirmNodeAsync(
        Guid nodeId,
        string confirmationCode,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await db.FarmNodes.SingleOrDefaultAsync(
            x => x.Id == nodeId && x.AccountId == identity.AccountId &&
                 x.DecommissionedAtUtc == null,
            cancellationToken)
            ?? throw new InvalidOperationException("Node não encontrado para esta conta.");
        if (node.ConnectionKind != "Agent" || node.ConfirmationCodeHash is null)
            throw new InvalidOperationException("Este computador não está aguardando confirmação.");
        if (node.ConfirmedAtUtc is not null)
            return await BuildDashboardAsync(db, identity.AccountId, cancellationToken);
        var normalized = confirmationCode?.Trim().ToUpperInvariant().Replace("-", "") ?? string.Empty;
        if (normalized.Length == 0 ||
            !string.Equals(
                node.ConfirmationCodeHash,
                FarmAgentSecrets.Hash(normalized),
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Chave de confirmação incorreta. Confira a chave exibida na janela do programa.");
        node.ConfirmedAtUtc = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return await BuildDashboardAsync(db, identity.AccountId, cancellationToken);
    }

    public async Task<FarmDashboard> RemoveNodeAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await db.FarmNodes.SingleOrDefaultAsync(
            x => x.Id == nodeId && x.AccountId == identity.AccountId &&
                 x.DecommissionedAtUtc == null,
            cancellationToken)
            ?? throw new InvalidOperationException("Node não encontrado para esta conta.");
        if (node.ConnectionKind != "Agent")
            throw new InvalidOperationException("O computador embutido da plataforma não pode ser removido.");
        // Descomissionamento (ADR 0038): credencial revogada para sempre; o
        // histórico de jobs permanece e o assento fica livre para re-vínculo.
        node.DecommissionedAtUtc = timeProvider.GetUtcNow();
        node.AgentCredentialHash = null;
        node.ConfirmationCodeHash = null;
        node.AgentAcceptJobs = false;
        node.SharePlatformWork = false;
        node.Status = FarmNodeStatus.Paused.ToString();
        await db.SaveChangesAsync(cancellationToken);
        return await BuildDashboardAsync(db, identity.AccountId, cancellationToken);
    }

    public async Task<IReadOnlyList<FarmJobSnapshot>> GetNodeJobsAsync(
        Guid nodeId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var bounded = Math.Clamp(limit, 1, 50);
        var jobs = await db.FarmJobs
            .Where(x => x.AccountId == identity.AccountId && x.NodeId == nodeId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(bounded)
            .ToArrayAsync(cancellationToken);
        var ids = jobs.Select(x => x.Id).ToArray();
        var events = await db.FarmJobEvents
            .Where(x => ids.Contains(x.JobId))
            .OrderBy(x => x.Sequence)
            .ToArrayAsync(cancellationToken);
        return jobs.Select(x => ToSnapshot(x, events.Where(e => e.JobId == x.Id))).ToArray();
    }

    public Task<FarmJobSnapshot> RunBenchmarkAsync(CancellationToken cancellationToken = default) =>
        RunWorkloadAsync(FarmWorkloadCatalog.DefaultId, cancellationToken);

    public Task<FarmJobSnapshot> RunWorkloadAsync(
        string workloadId,
        CancellationToken cancellationToken = default) =>
        RunWorkloadCoreAsync(workloadId, null, null, null, null, null, null, null, null, null, null, null, false, null, cancellationToken);

    internal Task<FarmJobSnapshot> RunApiWorkloadAsync(
        string workloadId,
        Guid tokenId,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        RunWorkloadCoreAsync(workloadId, null, tokenId, idempotencyKey, null, null, null, null, null, null, null, null, false, null, cancellationToken);

    internal Task<FarmJobSnapshot> RunUsefulWorkloadAsync(
        FarmUsefulJobSubmission submission,
        Guid? tokenId,
        string? idempotencyKey,
        CancellationToken cancellationToken = default) =>
        RunWorkloadCoreAsync(
            submission.Definition.Id, null, tokenId, idempotencyKey,
            null, null, null, null, null, null, null, null, false, submission, cancellationToken);

    internal Task<FarmJobSnapshot> RunScheduledWorkloadAsync(
        Guid accountId,
        string workloadId,
        Guid scheduleId,
        DateTimeOffset scheduledForUtc,
        CancellationToken cancellationToken = default) =>
        RunWorkloadCoreAsync(
            workloadId,
            accountId,
            null,
            null,
            scheduleId,
            scheduledForUtc.ToUniversalTime(),
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            cancellationToken);

    /// <summary>
    /// Farm contínua (ADR 0036): um Agent ocioso pediu trabalho e a conta
    /// autorizou consumo — cria um job marathon pinado no próprio node.
    /// </summary>
    public Task<FarmJobSnapshot> RunContinuousWorkloadAsync(
        Guid accountId,
        Guid nodeId,
        CancellationToken cancellationToken = default) =>
        RunWorkloadCoreAsync(
            FarmWorkloadCatalog.ContinuousId, accountId, null, null, null, null,
            null, null, null, null, null, nodeId, false, null, cancellationToken);

    internal Task<FarmJobSnapshot> RunQueuedDemandAsync(
        Guid accountId,
        Guid demandId,
        int shardIndex,
        int shardCount,
        int shardAttempt,
        Guid? excludedNodeId,
        FarmUsefulJobSubmission submission,
        bool platformPool = false,
        CancellationToken cancellationToken = default) =>
        RunWorkloadCoreAsync(
            submission.Definition.Id, accountId, null, null, null, null,
            demandId, shardIndex, shardCount, shardAttempt, excludedNodeId,
            null, platformPool, submission, cancellationToken);

    private async Task<FarmJobSnapshot> RunWorkloadCoreAsync(
        string workloadId,
        Guid? explicitAccountId,
        Guid? apiTokenId,
        string? apiIdempotencyKey,
        Guid? scheduleId,
        DateTimeOffset? scheduledForUtc,
        Guid? demandId,
        int? demandShardIndex,
        int? demandShardCount,
        int? demandShardAttempt,
        Guid? excludedNodeId,
        Guid? targetNodeId,
        bool platformPool,
        FarmUsefulJobSubmission? useful,
        CancellationToken cancellationToken)
    {
        var definition = useful?.Definition ?? FarmWorkloadCatalog.GetRequired(workloadId);
        if (definition.Id != workloadId)
            throw new ArgumentException("A entrada útil não corresponde ao workload solicitado.", nameof(workloadId));
        if (useful is null && definition.InputSchema is not null)
            throw new ArgumentException("Este workload exige uma demanda tipada.", nameof(workloadId));
        if (demandId.HasValue != demandShardIndex.HasValue || demandId.HasValue != demandShardCount.HasValue ||
            demandId.HasValue != demandShardAttempt.HasValue ||
            (demandShardIndex.HasValue && (demandShardIndex < 1 || demandShardCount < demandShardIndex ||
                                           demandShardAttempt < 1)))
            throw new ArgumentException("A atribuição do shard da demanda é inválida.", nameof(demandId));
        var accountId = explicitAccountId ??
            (await identityAccessor.GetRequiredIdentityAsync(cancellationToken)).AccountId;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var schedulingTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({SchedulingLockKey(accountId)})",
            cancellationToken);
        if (apiTokenId is Guid tokenId && apiIdempotencyKey is not null)
        {
            var replay = await db.FarmJobs.AsNoTracking().SingleOrDefaultAsync(
                x => x.AccountId == accountId &&
                     x.ApiTokenId == tokenId &&
                     x.ApiIdempotencyKey == apiIdempotencyKey,
                cancellationToken);
            if (replay is not null)
            {
                if (replay.Workload != definition.Workload || replay.WorkloadVersion != definition.Version ||
                    replay.InputDigest != useful?.InputDigest)
                    throw new InvalidOperationException(
                        "A chave de idempotência já foi usada com outro workload.");
                return await LoadJobAsync(db, replay.Id, cancellationToken);
            }
        }
        if (scheduleId is Guid recurringScheduleId && scheduledForUtc is DateTimeOffset occurrence)
        {
            var replay = await db.FarmJobs.AsNoTracking().SingleOrDefaultAsync(
                x => x.AccountId == accountId &&
                     x.ScheduleId == recurringScheduleId &&
                     x.ScheduledForUtc == occurrence,
                cancellationToken);
            if (replay is not null)
                return await LoadJobAsync(db, replay.Id, cancellationToken);
        }
        if (demandId is Guid queuedDemandId)
        {
            // O replay do shard é global por (demanda, shard, tentativa): no
            // pool da plataforma a tentativa anterior pode pertencer a outro
            // dono de node (ADR 0031), e o índice único garante a unicidade.
            var replay = await db.FarmJobs.AsNoTracking().SingleOrDefaultAsync(
                x => x.DemandId == queuedDemandId &&
                     x.DemandShardIndex == demandShardIndex &&
                     x.DemandShardAttempt == demandShardAttempt,
                cancellationToken);
            if (replay is not null)
                return await LoadJobAsync(db, replay.Id, cancellationToken);
        }
        await EnsureEmbeddedNodeAsync(db, accountId, cancellationToken);
        await FarmJobLifecycle.ExpireAsync(
            db,
            timeProvider.GetUtcNow(),
            cancellationToken,
            accountId: accountId);
        var node = await SelectExecutionNodeAsync(
            db, accountId, definition, excludedNodeId, targetNodeId, platformPool, cancellationToken);
        var isAgent = node.ConnectionKind == FarmNodeConnectionKind.Agent.ToString();

        var now = timeProvider.GetUtcNow();
        var jobId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var manifestVersion = isAgent ? RemoteManifestVersion : ManifestVersion;
        var manifestSigning = isAgent
            ? signingOptions.DeriveForAgent(node.Id).Signing
            : signingOptions;
        var signedManifest = FarmManifestSecurity.Sign(
            new FarmJobManifest(
                manifestVersion,
                jobId,
                attemptId,
                node.Id,
                definition.Workload,
                definition.Version,
                definition.VerificationPolicy,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                definition.Iterations,
                definition.TimeoutSeconds,
                definition.RequiredLogicalProcessors,
                definition.RequiredMemoryBytes,
                definition.NetworkPolicy,
                definition.DataClassification,
                now,
                now.AddMinutes(5),
                FarmNodeProfileFingerprint.Compute(
                    node.Runtime,
                    node.LogicalProcessors,
                    node.MemoryBytes,
                    node.AgentVersion),
                useful?.InputDigest),
            manifestSigning);
        var job = new FarmJobRecord
        {
            Id = jobId,
            AccountId = accountId,
            NodeId = node.Id,
            ReservedLogicalProcessors = signedManifest.Manifest.MaxLogicalProcessors,
            ReservedMemoryBytes = signedManifest.Manifest.MaxMemoryBytes,
            ApiTokenId = apiTokenId,
            ApiIdempotencyKey = apiIdempotencyKey,
            ScheduleId = scheduleId,
            ScheduledForUtc = scheduledForUtc,
            DemandId = demandId,
            DemandShardIndex = demandShardIndex,
            DemandShardAttempt = demandShardAttempt,
            DemandShardCount = demandShardCount,
            DemandLabel = useful?.Label,
            InputVersion = useful?.InputVersion,
            InputPayload = useful?.InputPayload,
            InputSizeBytes = useful?.InputSizeBytes,
            InputDigest = useful?.InputDigest,
            Workload = definition.Workload,
            WorkloadVersion = definition.Version,
            VerificationPolicy = definition.VerificationPolicy,
            Nonce = signedManifest.Manifest.Nonce,
            AttemptId = attemptId,
            ManifestVersion = signedManifest.Manifest.Version,
            ManifestPayload = signedManifest.Payload,
            ManifestDigest = signedManifest.Digest,
            ManifestSignature = signedManifest.Signature,
            ManifestIssuedAtUtc = signedManifest.Manifest.IssuedAtUtc,
            ManifestExpiresAtUtc = signedManifest.Manifest.ExpiresAtUtc,
            Status = FarmJobStatus.Queued.ToString(),
            CreatedAtUtc = now
        };
        db.FarmJobs.Add(job);
        AddEvent(db, job, FarmJobStatus.Queued, now,
            demandId is not null
                ? $"Shard {demandShardIndex}/{demandShardCount} da demanda {demandId:N} vinculado ao manifesto assinado."
                : scheduleId is null
                ? useful is null
                    ? $"Manifest do catálogo {FarmWorkloadCatalog.Version} criado pelo scheduler."
                    : $"Demanda útil '{useful.Label}' vinculada ao manifesto assinado."
                : $"Manifest do catálogo {FarmWorkloadCatalog.Version} criado pelo agendamento {scheduleId:N}.");
        if (isAgent)
        {
            job.Status = FarmJobStatus.Offered.ToString();
            AddEvent(db, job, FarmJobStatus.Offered, now, "Job assinado oferecido ao Compute Agent vinculado.");
        }
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception)
        {
            if (apiTokenId is Guid replayTokenId && apiIdempotencyKey is not null)
            {
                await using var replayDb = await factory.CreateDbContextAsync(cancellationToken);
                var replay = await replayDb.FarmJobs.AsNoTracking().SingleOrDefaultAsync(
                    x => x.AccountId == accountId &&
                         x.ApiTokenId == replayTokenId &&
                         x.ApiIdempotencyKey == apiIdempotencyKey,
                    cancellationToken);
                if (replay is not null)
                {
                    if (replay.Workload != definition.Workload || replay.WorkloadVersion != definition.Version ||
                        replay.InputDigest != useful?.InputDigest)
                        throw new InvalidOperationException(
                            "A chave de idempotência já foi usada com outro workload.", exception);
                    return await LoadJobAsync(replayDb, replay.Id, cancellationToken);
                }
            }
            if (scheduleId is Guid replayScheduleId &&
                scheduledForUtc is DateTimeOffset replayOccurrence)
            {
                await using var replayDb = await factory.CreateDbContextAsync(cancellationToken);
                var replay = await replayDb.FarmJobs.AsNoTracking().SingleOrDefaultAsync(
                    x => x.AccountId == accountId &&
                         x.ScheduleId == replayScheduleId &&
                         x.ScheduledForUtc == replayOccurrence,
                    cancellationToken);
                if (replay is not null)
                    return await LoadJobAsync(replayDb, replay.Id, cancellationToken);
            }
            if (demandId is Guid replayDemandId)
            {
                await using var replayDb = await factory.CreateDbContextAsync(cancellationToken);
                var replay = await replayDb.FarmJobs.AsNoTracking().SingleOrDefaultAsync(
                    x => x.AccountId == accountId && x.DemandId == replayDemandId &&
                         x.DemandShardIndex == demandShardIndex &&
                         x.DemandShardAttempt == demandShardAttempt,
                    cancellationToken);
                if (replay is not null)
                    return await LoadJobAsync(replayDb, replay.Id, cancellationToken);
            }
            throw new InvalidOperationException(
                "Não foi possível enfileirar o Job; pode existir outra execução ativa.",
                exception);
        }

        await schedulingTransaction.CommitAsync(cancellationToken);

        if (isAgent)
            return await LoadJobAsync(db, job.Id, cancellationToken);

        try
        {
            var manifest = GetVerifiedManifestForExecution(job, timeProvider.GetUtcNow());
            Transition(db, job, FarmJobStatus.Running, "Runtime autorizado iniciou o workload.");
            job.StartedAtUtc = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(manifest.TimeoutSeconds));
            var stopwatch = Stopwatch.StartNew();
            var execution = await Task.Run(
                () => FarmDeterministicWorkloads.Execute(
                    manifest.Workload,
                    manifest.Nonce,
                    manifest.Iterations,
                    job.InputPayload,
                    timeout.Token),
                timeout.Token);
            stopwatch.Stop();

            Transition(db, job, FarmJobStatus.Verifying, "Resultado recebido; reexecução determinística iniciada.");
            job.DurationMilliseconds = Math.Max(1, stopwatch.ElapsedMilliseconds);
            job.ResultDigest = execution.Digest;
            if (execution.OutputPayload is not null)
            {
                job.OutputVersion = definition.OutputSchema;
                job.OutputPayload = execution.OutputPayload;
                job.OutputSizeBytes = Encoding.UTF8.GetByteCount(execution.OutputPayload);
                job.OutputDigest = execution.Digest;
            }
            await db.SaveChangesAsync(cancellationToken);

            var expected = await Task.Run(
                () => FarmDeterministicWorkloads.Execute(
                    manifest.Workload,
                    manifest.Nonce,
                    manifest.Iterations,
                    job.InputPayload,
                    timeout.Token),
                timeout.Token);
            job.ExpectedDigest = expected.Digest;
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(execution.Digest),
                    Convert.FromHexString(expected.Digest)) ||
                !string.Equals(execution.OutputPayload, expected.OutputPayload, StringComparison.Ordinal))
                throw new InvalidOperationException("O resultado do workload não passou na verificação.");

            job.Status = FarmJobStatus.Completed.ToString();
            job.ComputeUnitsMilli = definition.ProcessingPointsMilli;
            job.FinishedAtUtc = timeProvider.GetUtcNow();
            node.VerificationLevel = FarmVerificationLevel.Verified.ToString();
            node.VerifiedProfileHash = FarmNodeProfileFingerprint.Compute(
                node.Runtime,
                node.LogicalProcessors,
                node.MemoryBytes,
                node.AgentVersion);
            node.ProfileVerifiedAtUtc = job.FinishedAtUtc;
            node.LastSeenAtUtc = job.FinishedAtUtc.Value;
            AddEvent(db, job, FarmJobStatus.Completed, job.FinishedAtUtc.Value, $"{definition.ProcessingPointsMilli / 1_000m:N3} CP verificado; AIC via resgate de recompensas.");
            await db.SaveChangesAsync(cancellationToken);
            await FarmEvidenceSecurity.SealAsync(db, job, signingOptions, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            // Auto-proposta (ADR 0042) — depois do selo da evidência.
            await rewardAutoProposer.TryProposeAsync(accountId, job.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await MarkTerminalAsync(
                db,
                job,
                FarmJobStatus.TimedOut,
                "O workload excedeu o timeout do manifesto.");
        }
        catch (OperationCanceledException)
        {
            await MarkTerminalAsync(
                db,
                job,
                FarmJobStatus.Cancelled,
                "A execução do workload foi cancelada.");
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkTerminalAsync(db, job, FarmJobStatus.Failed, exception.Message);
        }

        return await LoadJobAsync(db, job.Id, cancellationToken);
    }

    public async Task<FarmJobSnapshot?> GetJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty) return null;
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var exists = await db.FarmJobs.AsNoTracking().AnyAsync(
            x => x.Id == jobId && x.AccountId == identity.AccountId,
            cancellationToken);
        return exists ? await LoadJobAsync(db, jobId, cancellationToken) : null;
    }

    public async Task<FarmDashboard> CancelJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await FarmJobLifecycle.ExpireAsync(
            db,
            timeProvider.GetUtcNow(),
            cancellationToken,
            accountId: identity.AccountId);
        var job = await db.FarmJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == jobId && x.AccountId == identity.AccountId,
                cancellationToken)
            ?? throw new InvalidOperationException("Job não encontrado para esta conta.");
        var node = await db.FarmNodes.SingleAsync(x => x.Id == job.NodeId, cancellationToken);
        if (node.ConnectionKind != FarmNodeConnectionKind.Agent.ToString())
            throw new InvalidOperationException("Cancelamento remoto está disponível apenas para Jobs do Compute Agent.");
        if (job.Status == FarmJobStatus.Cancelled.ToString())
            return await BuildDashboardAsync(db, identity.AccountId, cancellationToken);
        if (job.CancellationRequestedAtUtc is not null)
            return await BuildDashboardAsync(db, identity.AccountId, cancellationToken);
        if (job.Status is not ("Offered" or "Accepted" or "Running"))
            throw new InvalidOperationException("Este Job não pode mais ser cancelado.");

        var now = timeProvider.GetUtcNow();
        if (job.Status == FarmJobStatus.Offered.ToString() &&
            await FarmJobLifecycle.TryTerminateAsync(
                db,
                job.Id,
                FarmJobStatus.Cancelled,
                "Oferta cancelada pelo usuário antes do aceite do Agent; nenhum CP ou AIC emitido.",
                now,
                cancellationToken))
            return await BuildDashboardAsync(db, identity.AccountId, cancellationToken);

        var requested = await db.FarmJobs
            .Where(x => x.Id == job.Id &&
                        (x.Status == "Accepted" || x.Status == "Running") &&
                        x.CancellationRequestedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.CancellationRequestedAtUtc, now),
                cancellationToken);
        if (requested != 1)
            throw new InvalidOperationException("O estado do Job mudou antes do pedido de cancelamento.");
        var current = await db.FarmJobs.AsNoTracking()
            .SingleAsync(x => x.Id == job.Id, cancellationToken);
        var sequence = await db.FarmJobEvents
            .Where(x => x.JobId == job.Id)
            .MaxAsync(x => (int?)x.Sequence, cancellationToken) ?? 0;
        db.FarmJobEvents.Add(new FarmJobEventRecord
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            Sequence = sequence + 1,
            Status = current.Status,
            Detail = "Cancelamento solicitado pelo usuário; aguardando confirmação do Compute Agent.",
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return await BuildDashboardAsync(db, identity.AccountId, cancellationToken);
    }

    public async Task<FarmJobEvidenceDownload?> DownloadEvidenceAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var job = await db.FarmJobs.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == jobId && x.AccountId == identity.AccountId,
            cancellationToken);
        if (job is null || job.EvidencePayload is null) return null;
        var events = await db.FarmJobEvents.AsNoTracking()
            .Where(x => x.JobId == job.Id)
            .OrderBy(x => x.Sequence)
            .ToArrayAsync(cancellationToken);
        var verification = FarmEvidenceSecurity.Verify(job, events, signingOptions, out var evidence);
        if (!verification.IsValid || evidence is null)
            throw new InvalidOperationException("A integridade do bundle de evidências não pôde ser validada.");
        var content = FarmEvidenceSecurity.Export(
            evidence,
            job.EvidenceKeyId!,
            job.EvidenceDigest!,
            job.EvidenceSignature!);
        return new FarmJobEvidenceDownload(
            $"atos-farm-evidence-{job.Id:N}.json",
            content);
    }

    private async Task<FarmNodeRecord> EnsureEmbeddedNodeAsync(
        AIFarmDbContext db,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var node = await db.FarmNodes.SingleOrDefaultAsync(
            x => x.AccountId == accountId && x.ConnectionKind == "Embedded", cancellationToken);
        if (node is not null) return node;

        node = new FarmNodeRecord
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Name = $"LAB-{Environment.MachineName}".ToUpperInvariant(),
            Status = FarmNodeStatus.Online.ToString(),
            VerificationLevel = FarmVerificationLevel.Detected.ToString(),
            ConnectionKind = FarmNodeConnectionKind.Embedded.ToString(),
            Runtime = $"{RuntimeInformation.FrameworkDescription} // {RuntimeInformation.OSDescription}",
            LogicalProcessors = Environment.ProcessorCount,
            MemoryBytes = Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes),
            CreatedAtUtc = timeProvider.GetUtcNow(),
            LastSeenAtUtc = timeProvider.GetUtcNow()
        };
        db.FarmNodes.Add(node);
        await db.SaveChangesAsync(cancellationToken);
        return node;
    }

    private async Task<FarmNodeRecord> GetPrimaryNodeAsync(
        AIFarmDbContext db,
        Guid accountId,
        CancellationToken cancellationToken) =>
        await db.FarmNodes
            .Where(x => x.AccountId == accountId)
            .OrderByDescending(x => x.ConnectionKind == "Agent")
            .ThenBy(x => x.CreatedAtUtc)
            .FirstAsync(cancellationToken);

    private async Task<FarmNodeRecord> SelectExecutionNodeAsync(
        AIFarmDbContext db,
        Guid accountId,
        FarmWorkloadDefinition definition,
        Guid? excludedNodeId,
        Guid? targetNodeId,
        bool platformPool,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        // No pool da plataforma só concorrem Agents que o dono compartilhou
        // explicitamente; embedded nunca executa trabalho da plataforma. Um
        // node-alvo (farm contínua) restringe a disputa a ele mesmo — as
        // checagens de capacidade e confiabilidade continuam valendo.
        // Pendente (ADR 0038) CONTINUA na lista: ocupa o assento e mantém o
        // embedded suprimido — o bloqueio vem do acceptJobs=false do snapshot.
        // Descomissionado sai de verdade: o assento não existe mais.
        // Geração (ADR 0043) só vai para Agents com o runtime local detectado
        // — evita o job cair numa máquina sem Ollama, falhar e reencaminhar.
        var requiresGeneration = string.Equals(
            definition.Id, FarmWorkloadCatalog.GenerateId, StringComparison.Ordinal);
        var nodes = await db.FarmNodes
            .Where(x => x.AccountId == accountId &&
                x.DecommissionedAtUtc == null &&
                (targetNodeId == null || x.Id == targetNodeId) &&
                (!requiresGeneration || (x.ConnectionKind == "Agent" && x.SupportsGeneration)) &&
                (!platformPool ||
                 (x.ConnectionKind == "Agent" && x.SharePlatformWork)))
            .ToArrayAsync(cancellationToken);
        var reservations = await db.FarmJobs
            .Where(x => x.AccountId == accountId && FarmJobLifecycle.ActiveStatuses.Contains(x.Status))
            .GroupBy(x => x.NodeId)
            .Select(group => new
            {
                NodeId = group.Key,
                LogicalProcessors = group.Sum(x => x.ReservedLogicalProcessors),
                MemoryBytes = group.Sum(x => x.ReservedMemoryBytes)
            })
            .ToDictionaryAsync(x => x.NodeId, cancellationToken);
        var lastAssignments = await db.FarmJobs.Where(x => x.AccountId == accountId)
            .GroupBy(x => x.NodeId)
            .Select(group => new { NodeId = group.Key, LastAtUtc = group.Max(x => x.CreatedAtUtc) })
            .ToDictionaryAsync(x => x.NodeId, x => x.LastAtUtc, cancellationToken);
        var poolHasAgents = nodes.Any(x => x.ConnectionKind == FarmNodeConnectionKind.Agent.ToString());
        var reliabilities = await FarmNodeReliabilityProjection.LoadAsync(
            db, nodes.Select(x => x.Id).ToArray(), now, nodeReliabilityPolicy,
            cancellationToken);
        var evaluated = nodes.Select(node =>
        {
            var snapshot = ToSnapshot(node);
            reservations.TryGetValue(node.Id, out var reserved);
            var availableProcessors = Math.Max(0,
                snapshot.AuthorizedLogicalProcessors - (reserved?.LogicalProcessors ?? 0));
            var availableMemory = Math.Max(0,
                snapshot.AuthorizedMemoryBytes - (reserved?.MemoryBytes ?? 0));
            return new
            {
                Node = node,
                Snapshot = snapshot,
                Reliability = reliabilities[node.Id],
                AvailableProcessors = availableProcessors,
                AvailableMemory = availableMemory,
                Blocker = FarmCapacityEvaluator.Evaluate(
                    snapshot, definition, availableProcessors, availableMemory, poolHasAgents,
                    reliabilities[node.Id].IsQuarantined)
            };
        }).ToArray();
        var selected = evaluated
            .Where(x => x.Blocker == FarmCapacityBlocker.None &&
                        (!excludedNodeId.HasValue || x.Node.Id != excludedNodeId.Value))
            .OrderByDescending(x => x.Reliability.Score)
            .ThenBy(x => x.AvailableProcessors - definition.RequiredLogicalProcessors)
            .ThenBy(x => x.AvailableMemory - definition.RequiredMemoryBytes)
            .ThenBy(x => lastAssignments.GetValueOrDefault(x.Node.Id, DateTimeOffset.MinValue))
            .ThenBy(x => x.Node.Id)
            .Select(x => x.Node)
            .FirstOrDefault();
        if (selected is not null) return selected;
        var reasons = string.Join(", ", evaluated
            .Where(x => x.Blocker != FarmCapacityBlocker.EmbeddedSuppressed)
            .GroupBy(x => x.Blocker)
            .OrderBy(x => x.Key)
            .Select(x => $"{CapacityLabel(x.Key)}={x.Count()}"));
        var versionGuidance = evaluated.Any(x => x.Blocker == FarmCapacityBlocker.AgentVersionMismatch)
            ? $" Atualize o Compute Agent para {FarmAgentProtocol.CurrentAgentVersion}."
            : string.Empty;
        throw new InvalidOperationException(
            $"Nenhum Node atende {definition.RequiredLogicalProcessors} CPU / " +
            $"{definition.RequiredMemoryBytes / 1024 / 1024} MiB para {definition.DisplayName}. " +
            $"Bloqueios: {(string.IsNullOrEmpty(reasons) ? "POOL_VAZIO" : reasons)}." +
            versionGuidance);
    }

    private static string CapacityLabel(FarmCapacityBlocker blocker) => blocker switch
    {
        FarmCapacityBlocker.Paused => "PAUSADO",
        FarmCapacityBlocker.Offline => "OFFLINE",
        FarmCapacityBlocker.StaleHeartbeat => "HEARTBEAT_EXPIRADO",
        FarmCapacityBlocker.JobsDisabled => "POLÍTICA_LOCAL",
        FarmCapacityBlocker.AgentVersionMismatch => "VERSÃO_AGENT",
        FarmCapacityBlocker.Quarantined => "QUARENTENA_CONFIABILIDADE",
        FarmCapacityBlocker.InsufficientProcessors => "CPU_INSUFICIENTE",
        FarmCapacityBlocker.InsufficientMemory => "MEMÓRIA_INSUFICIENTE",
        FarmCapacityBlocker.Busy => "OCUPADO",
        _ => blocker.ToString().ToUpperInvariant()
    };

    private async Task<FarmDashboard> BuildDashboardAsync(
        AIFarmDbContext db,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var nodes = await db.FarmNodes.Where(x =>
                x.AccountId == accountId && x.DecommissionedAtUtc == null)
            .OrderByDescending(x => x.ConnectionKind == "Agent")
            .ThenBy(x => x.CreatedAtUtc)
            .ToArrayAsync(cancellationToken);
        var node = nodes[0];
        var jobs = await db.FarmJobs
            .Where(x => x.AccountId == accountId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(10)
            .ToArrayAsync(cancellationToken);
        var ids = jobs.Select(x => x.Id).ToArray();
        var events = await db.FarmJobEvents
            .Where(x => ids.Contains(x.JobId))
            .OrderBy(x => x.Sequence)
            .ToArrayAsync(cancellationToken);
        var lifetimeByNode = await db.FarmJobs
            .Where(x => x.AccountId == accountId && x.Status == "Completed")
            .GroupBy(x => x.NodeId)
            .Select(group => new { NodeId = group.Key, Milli = group.Sum(x => x.ComputeUnitsMilli) })
            .ToDictionaryAsync(x => x.NodeId, x => x.Milli, cancellationToken);
        // Sparkline dos cards (ADR 0039): CP verificado por minuto, 30 min.
        var seriesNowUtc = timeProvider.GetUtcNow();
        var seriesStartUtc = seriesNowUtc.AddMinutes(-30);
        var seriesJobs = await db.FarmJobs.AsNoTracking()
            .Where(x => x.AccountId == accountId &&
                        x.Status == "Completed" &&
                        x.FinishedAtUtc != null &&
                        x.FinishedAtUtc > seriesStartUtc)
            .Select(x => new { x.NodeId, x.FinishedAtUtc, x.ComputeUnitsMilli })
            .ToArrayAsync(cancellationToken);
        var nodeCpSeries = seriesJobs
            .GroupBy(x => x.NodeId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var buckets = new long[30];
                    foreach (var job in group)
                    {
                        var minute = (int)Math.Clamp(
                            Math.Floor((job.FinishedAtUtc!.Value - seriesStartUtc).TotalMinutes),
                            0, 29);
                        buckets[minute] += job.ComputeUnitsMilli;
                    }
                    return (IReadOnlyList<long>)buckets;
                });
        return new FarmDashboard(
            ToSnapshot(node, lifetimeByNode.GetValueOrDefault(node.Id)),
            jobs.Select(x => ToSnapshot(x, events.Where(e => e.JobId == x.Id))).ToArray(),
            nodes.Select(x => ToSnapshot(x, lifetimeByNode.GetValueOrDefault(x.Id))).ToArray(),
            nodeCpSeries);
    }

    private async Task<FarmJobSnapshot> LoadJobAsync(
        AIFarmDbContext db,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var job = await db.FarmJobs.SingleAsync(x => x.Id == jobId, cancellationToken);
        var events = await db.FarmJobEvents
            .Where(x => x.JobId == jobId)
            .OrderBy(x => x.Sequence)
            .ToArrayAsync(cancellationToken);
        return ToSnapshot(job, events);
    }

    private FarmJobManifest GetVerifiedManifestForExecution(
        FarmJobRecord job,
        DateTimeOffset now)
    {
        if (!TryReadVerifiedManifest(job, out var manifest) || manifest is null)
            throw new InvalidOperationException("O manifesto do Job não possui assinatura válida.");
        if (manifest.ExpiresAtUtc <= now)
            throw new InvalidOperationException("O manifesto do Job expirou antes da execução.");
        var definition = FarmWorkloadCatalog.Find(manifest.Workload, manifest.WorkloadVersion);
        if (definition is null ||
            manifest.VerificationPolicy != definition.VerificationPolicy ||
            manifest.Iterations != definition.Iterations ||
            manifest.TimeoutSeconds != definition.TimeoutSeconds ||
            manifest.MaxLogicalProcessors != definition.MaxLogicalProcessors ||
            manifest.MaxMemoryBytes <= 0 ||
            manifest.MaxMemoryBytes > definition.MaxMemoryBytes ||
            manifest.NetworkPolicy != definition.NetworkPolicy ||
            manifest.DataClassification != definition.DataClassification)
            throw new InvalidOperationException("O manifesto do Job viola a allowlist do laboratório.");
        if (manifest.InputDigest != job.InputDigest || !InputMatchesDefinition(job, definition))
            throw new InvalidOperationException("A entrada do Job não corresponde ao manifesto.");
        return manifest;
    }

    private static bool InputMatchesDefinition(
        FarmJobRecord job,
        FarmWorkloadDefinition definition)
    {
        if (definition.InputSchema is null)
            return job.InputVersion is null && job.InputPayload is null && job.InputDigest is null;
        if (job.InputVersion != definition.InputSchema || job.InputPayload is null ||
            job.InputSizeBytes != Encoding.UTF8.GetByteCount(job.InputPayload))
            return false;
        try { return job.InputDigest == FarmDeterministicWorkloads.ComputeInputDigest(job.InputPayload); }
        catch (ArgumentException) { return false; }
    }

    private static long SchedulingLockKey(Guid accountId) =>
        BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(accountId.ToByteArray()));

    private bool TryReadVerifiedManifest(
        FarmJobRecord job,
        out FarmJobManifest? manifest)
    {
        return FarmManifestSecurity.TryVerifyJobBinding(job, signingOptions, out manifest);
    }

    private async Task MarkTerminalAsync(
        AIFarmDbContext db,
        FarmJobRecord job,
        FarmJobStatus status,
        string reason)
    {
        job.Status = status.ToString();
        job.FailureReason = reason;
        job.FinishedAtUtc = timeProvider.GetUtcNow();
        AddEvent(db, job, status, job.FinishedAtUtc.Value, $"{reason} Nenhum CP ou AIC emitido.");
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private void Transition(
        AIFarmDbContext db,
        FarmJobRecord job,
        FarmJobStatus status,
        string detail)
    {
        job.Status = status.ToString();
        AddEvent(db, job, status, timeProvider.GetUtcNow(), detail);
    }

    private static void AddEvent(
        AIFarmDbContext db,
        FarmJobRecord job,
        FarmJobStatus status,
        DateTimeOffset occurredAtUtc,
        string detail) =>
        db.FarmJobEvents.Add(new FarmJobEventRecord
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            Sequence = db.ChangeTracker.Entries<FarmJobEventRecord>().Count(x => x.Entity.JobId == job.Id) + 1,
            Status = status.ToString(),
            Detail = detail,
            OccurredAtUtc = occurredAtUtc
        });

    private FarmNodeSnapshot ToSnapshot(FarmNodeRecord node, long lifetimeComputeUnitsMilli = 0)
    {
        var connectionKind = Enum.TryParse<FarmNodeConnectionKind>(node.ConnectionKind, out var parsedKind)
            ? parsedKind
            : FarmNodeConnectionKind.Embedded;
        var heartbeatFresh = connectionKind == FarmNodeConnectionKind.Embedded ||
            timeProvider.GetUtcNow() - node.LastSeenAtUtc <= AgentHeartbeatTolerance;
        var persistedStatus = Enum.Parse<FarmNodeStatus>(node.Status);
        var effectiveStatus = heartbeatFresh ? persistedStatus : FarmNodeStatus.Offline;
        // Assento pendente (ADR 0038): vinculado, mas o dono ainda nao digitou
        // a chave de confirmacao no site - nao aceita trabalho.
        var pendingConfirmation = connectionKind == FarmNodeConnectionKind.Agent &&
            node.ConfirmedAtUtc is null;
        var acceptJobs = effectiveStatus == FarmNodeStatus.Online &&
            !pendingConfirmation &&
            (connectionKind == FarmNodeConnectionKind.Embedded || node.AgentAcceptJobs);
        return new FarmNodeSnapshot(
            node.Id,
            node.Name,
            effectiveStatus,
            Enum.Parse<FarmVerificationLevel>(node.VerificationLevel),
            node.Runtime,
            node.LogicalProcessors,
            node.MemoryBytes,
            node.LastSeenAtUtc,
            connectionKind,
            node.AgentVersion,
            heartbeatFresh,
            acceptJobs,
            node.AgentMaxLogicalProcessors ?? node.LogicalProcessors,
            node.AgentMaxMemoryBytes ?? node.MemoryBytes,
            node.VerifiedProfileHash,
            node.ProfileVerifiedAtUtc,
            node.SharePlatformWork,
            pendingConfirmation,
            heartbeatFresh ? node.LastWorkerCpuPercent : null,
            heartbeatFresh ? node.LastWorkerMemoryBytes : null,
            lifetimeComputeUnitsMilli,
            node.SupportsGeneration);
    }

    internal FarmJobSnapshot ToSnapshot(
        FarmJobRecord job,
        IEnumerable<FarmJobEventRecord> events)
    {
        var eventRecords = events.OrderBy(x => x.Sequence).ToArray();
        FarmJobManifestSnapshot? manifestSnapshot = null;
        var isValid = TryReadVerifiedManifest(job, out var manifest);
        if (manifest is not null &&
            job.ManifestDigest is not null &&
            job.ManifestSignature is not null)
        {
            manifestSnapshot = new FarmJobManifestSnapshot(
                manifest.Version,
                manifest.AttemptId,
                job.ManifestDigest,
                job.ManifestSignature,
                isValid,
                manifest.IssuedAtUtc,
                manifest.ExpiresAtUtc,
                manifest.TimeoutSeconds,
                manifest.Iterations,
                manifest.MaxLogicalProcessors,
                manifest.MaxMemoryBytes,
                manifest.NetworkPolicy,
                manifest.DataClassification,
                manifest.NodeProfileHash,
                manifest.InputDigest);
        }

        return new FarmJobSnapshot(
            job.Id,
            job.NodeId,
            job.Workload,
            job.WorkloadVersion,
            job.VerificationPolicy,
            Enum.Parse<FarmJobStatus>(job.Status),
            job.ComputeUnitsMilli,
            job.DurationMilliseconds,
            job.ResultDigest,
            job.CreatedAtUtc,
            job.FinishedAtUtc,
            job.CancellationRequestedAtUtc,
            job.AgentLeaseExpiresAtUtc,
            job.WorkerPeakMemoryBytes,
            job.WorkerProcessorTimeMilliseconds,
            job.WorkerEnforcement,
            manifestSnapshot,
            FarmEvidenceSecurity.ToSnapshot(job, eventRecords, signingOptions),
            eventRecords.Select(x => new FarmJobEventSnapshot(
                x.Sequence,
                Enum.Parse<FarmJobStatus>(x.Status),
                x.OccurredAtUtc,
                x.Detail)).ToArray(),
            job.InputVersion is null ? null : new FarmUsefulJobSnapshot(
                job.DemandLabel!,
                job.InputVersion,
                job.InputSizeBytes!.Value,
                job.InputDigest!,
                job.OutputVersion,
                job.OutputSizeBytes,
                job.OutputDigest));
    }
}

internal static class DeterministicBenchmark
{
    public static string Compute(
        string nonce,
        int iterations,
        CancellationToken cancellationToken = default)
        => FarmDeterministicWorkloads.Compute(
            FarmWorkloadCatalog.GetRequired(FarmWorkloadCatalog.DefaultId).Workload,
            nonce,
            iterations,
            cancellationToken);
}
