using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

internal sealed class PostgresFarmAgentService(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    TimeProvider timeProvider,
    FarmManifestSigningOptions signingOptions,
    FarmRewardPolicy rewardPolicy,
    IFarmContinuousDispatcher continuousDispatcher,
    FarmContinuousOptions continuousOptions,
    FarmRewardAutoProposer rewardAutoProposer) : IFarmAgentService
{
    internal const int HeartbeatIntervalSeconds = 30;
    internal const int ActiveHeartbeatIntervalSeconds = 2;
    internal static readonly TimeSpan AgentLeaseLifetime = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(10);
    private const string SignatureAlgorithm = "HMAC-SHA256-NODE-V1";

    public async Task<FarmAgentEnrollment> CreateEnrollmentAsync(
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        await db.FarmAgentEnrollments
            .Where(x => x.AccountId == identity.AccountId && x.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.ConsumedAtUtc, now),
                cancellationToken);

        var token = CreateSecret("afe");
        var enrollment = new FarmAgentEnrollmentRecord
        {
            Id = Guid.NewGuid(),
            AccountId = identity.AccountId,
            TokenHash = HashSecret(token),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(EnrollmentLifetime)
        };
        db.FarmAgentEnrollments.Add(enrollment);
        await db.SaveChangesAsync(cancellationToken);
        return new FarmAgentEnrollment(enrollment.Id, token, enrollment.ExpiresAtUtc);
    }

    public async Task<FarmAgentEnrollmentResult?> EnrollAsync(
        string token,
        FarmAgentEnrollmentRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = Validate(request);
        if (string.IsNullOrWhiteSpace(token)) return null;
        // Handshake (ADR 0038): sem e-mail nao ha vinculo — e a checagem vem
        // ANTES de consumir o codigo de uso unico, para nao queimar por typo.
        if (string.IsNullOrWhiteSpace(normalized.AccountEmail))
            throw new ArgumentException(
                "Informe o e-mail da conta no vínculo — atualize o programa para a versão atual.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var tokenHash = HashSecret(token);
        var consumed = await db.FarmAgentEnrollments
            .Where(x => x.TokenHash == tokenHash &&
                        x.ConsumedAtUtc == null &&
                        x.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.ConsumedAtUtc, now),
                cancellationToken);
        if (consumed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var enrollment = await db.FarmAgentEnrollments
            .SingleAsync(x => x.TokenHash == tokenHash, cancellationToken);
        var ownerEmail = await db.Users.AsNoTracking()
            .Where(x => x.Id == enrollment.AccountId)
            .Select(x => x.Email)
            .SingleOrDefaultAsync(cancellationToken);
        if (ownerEmail is not null &&
            !string.Equals(
                ownerEmail.Trim(),
                normalized.AccountEmail.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            // O codigo ja foi consumido de proposito: um par codigo+e-mail
            // errado queima o codigo e o dono gera outro no assento.
            await transaction.CommitAsync(cancellationToken);
            throw new ArgumentException(
                "O e-mail informado não corresponde à conta que gerou o código. Gere um novo código no site e confira o e-mail.");
        }
        var node = await db.FarmNodes
            .SingleOrDefaultAsync(
                x => x.AccountId == enrollment.AccountId &&
                     x.ConnectionKind == "Agent" &&
                     x.DecommissionedAtUtc == null &&
                     x.Name == normalized.Name,
                cancellationToken);
        if (node is null)
        {
            node = new FarmNodeRecord
            {
                Id = Guid.NewGuid(),
                AccountId = enrollment.AccountId,
                Name = normalized.Name,
                ConnectionKind = FarmNodeConnectionKind.Agent.ToString(),
                CreatedAtUtc = now
            };
            db.FarmNodes.Add(node);
        }

        var credential = CreateSecret("afc");
        var confirmationCode = FarmAgentSecrets.CreateConfirmationCode();
        node.ConfirmationCodeHash = FarmAgentSecrets.Hash(
            FarmAgentSecrets.NormalizeConfirmationCode(confirmationCode));
        // Todo vinculo (novo ou re-vinculo) nasce pendente: a chave acima
        // precisa ser digitada no site para o node voltar a farmar.
        node.ConfirmedAtUtc = null;
        node.LastWorkerCpuPercent = null;
        node.LastWorkerMemoryBytes = null;
        node.Name = normalized.Name;
        node.Runtime = normalized.Runtime;
        node.LogicalProcessors = normalized.LogicalProcessors;
        node.MemoryBytes = normalized.MemoryBytes;
        node.AgentVersion = normalized.AgentVersion;
        node.AgentCredentialHash = HashSecret(credential);
        node.AgentCredentialIssuedAtUtc = now;
        node.AgentAcceptJobs = true;
        node.AgentMaxLogicalProcessors = Math.Min(1, normalized.LogicalProcessors);
        node.AgentMaxMemoryBytes = Math.Min(256L * 1024 * 1024, normalized.MemoryBytes);
        node.ConnectionKind = FarmNodeConnectionKind.Agent.ToString();
        node.Status = FarmNodeStatus.Online.ToString();
        node.VerificationLevel = FarmVerificationLevel.Detected.ToString();
        node.VerifiedProfileHash = null;
        node.ProfileVerifiedAtUtc = null;
        node.LastSeenAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new FarmAgentEnrollmentResult(
            node.Id, credential, HeartbeatIntervalSeconds, confirmationCode);
    }

    public async Task<FarmAgentHeartbeatResult?> HeartbeatAsync(
        string credential,
        FarmAgentHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = Validate(request);
        if (string.IsNullOrWhiteSpace(credential)) return null;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var credentialHash = HashSecret(credential);
        var node = await db.FarmNodes.SingleOrDefaultAsync(
            x => x.AgentCredentialHash == credentialHash && x.ConnectionKind == "Agent",
            cancellationToken);
        if (node is null) return null;

        var now = timeProvider.GetUtcNow();
        var observedProfileHash = FarmNodeProfileFingerprint.Compute(
            normalized.Runtime,
            normalized.LogicalProcessors,
            normalized.MemoryBytes,
            normalized.AgentVersion);
        if (!string.Equals(
                node.VerifiedProfileHash,
                observedProfileHash,
                StringComparison.Ordinal))
        {
            node.VerificationLevel = FarmVerificationLevel.Detected.ToString();
            node.VerifiedProfileHash = null;
            node.ProfileVerifiedAtUtc = null;
        }
        node.Runtime = normalized.Runtime;
        node.LogicalProcessors = normalized.LogicalProcessors;
        node.MemoryBytes = normalized.MemoryBytes;
        node.AgentVersion = normalized.AgentVersion;
        node.AgentAcceptJobs = normalized.AcceptJobs;
        node.AgentMaxLogicalProcessors = normalized.MaxLogicalProcessors;
        node.AgentMaxMemoryBytes = normalized.MaxMemoryBytes;
        // Capacidade de geração detectada pelo Agent (ADR 0043): só quem tem o
        // runtime local recebe IRIS_GENERATE.
        node.SupportsGeneration = normalized.SupportsGeneration;
        if (normalized.WorkerCpuPercent is double workerCpu)
            node.LastWorkerCpuPercent = Math.Clamp(workerCpu, 0, 100d * 1024);
        if (normalized.WorkerMemoryBytes is long workerMemory)
            node.LastWorkerMemoryBytes = Math.Max(0, workerMemory);
        node.LastSeenAtUtc = now;
        if (node.Status == FarmNodeStatus.Offline.ToString())
            node.Status = FarmNodeStatus.Online.ToString();

        DateTimeOffset? leaseExpiresAtUtc = null;
        var activeJobIds = NormalizeActiveJobIds(normalized.ActiveJobId, normalized.ActiveJobIds);
        var cancelJobIds = new List<Guid>();
        if (activeJobIds.Length > 0)
        {
            await FarmJobLifecycle.ExpireAsync(
                db,
                now,
                cancellationToken,
                nodeId: node.Id);
            var activeJobs = await db.FarmJobs.Where(
                x => activeJobIds.Contains(x.Id) && x.NodeId == node.Id &&
                     (x.Status == "Running" ||
                      x.Status == "Uploading" ||
                      x.Status == "Verifying"))
                .ToArrayAsync(cancellationToken);
            foreach (var jobId in activeJobIds)
            {
                var activeJob = activeJobs.SingleOrDefault(x => x.Id == jobId);
                if (activeJob is null || activeJob.CancellationRequestedAtUtc is not null)
                {
                    cancelJobIds.Add(jobId);
                    continue;
                }
                activeJob.AgentLeaseRenewedAtUtc = now;
                activeJob.AgentLeaseExpiresAtUtc = now.Add(AgentLeaseLifetime);
            }
            leaseExpiresAtUtc = activeJobs.Where(x => x.AgentLeaseExpiresAtUtc is not null)
                .Select(x => x.AgentLeaseExpiresAtUtc).Max();
        }
        await db.SaveChangesAsync(cancellationToken);

        var status = Enum.Parse<FarmNodeStatus>(node.Status);
        var pendingConfirmation = node.ConfirmedAtUtc is null;
        return new FarmAgentHeartbeatResult(
            node.Id,
            status,
            status == FarmNodeStatus.Online && node.AgentAcceptJobs && !pendingConfirmation,
            now,
            HeartbeatIntervalSeconds,
            ActiveHeartbeatIntervalSeconds,
            activeJobIds.FirstOrDefault() == Guid.Empty ? null : activeJobIds[0],
            leaseExpiresAtUtc,
            cancelJobIds.Count > 0,
            cancelJobIds,
            pendingConfirmation);
    }

    public async Task<FarmAgentTrust?> GetTrustAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await AuthenticateNodeAsync(db, credential, cancellationToken);
        if (node is null) return null;
        var key = signingOptions.DeriveForAgent(node.Id);
        return new FarmAgentTrust(SignatureAlgorithm, key.VerificationKey, key.KeyId);
    }

    public async Task<FarmAgentIdentity?> GetIdentityAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await AuthenticateNodeAsync(db, credential, cancellationToken);
        if (node is null) return null;
        var account = await db.Accounts
            .AsNoTracking()
            .SingleAsync(x => x.Id == node.AccountId, cancellationToken);
        var lifetimeComputeUnitsMilli = await db.FarmJobs
            .Where(x => x.NodeId == node.Id && x.Status == "Completed")
            .SumAsync(x => (long?)x.ComputeUnitsMilli, cancellationToken) ?? 0;
        return new FarmAgentIdentity(
            node.Id,
            node.Name,
            account.DisplayName,
            node.SharePlatformWork,
            node.Status,
            node.VerificationLevel,
            node.CreatedAtUtc,
            node.AgentCredentialIssuedAtUtc,
            rewardPolicy.IsEnabled,
            rewardPolicy.IsEnabled ? rewardPolicy.AicMinorPerThousandCp : 0,
            lifetimeComputeUnitsMilli);
    }

    public async Task<FarmAgentCredentialRotation?> RotateCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await AuthenticateNodeAsync(db, credential, cancellationToken);
        if (node is null) return null;
        var now = timeProvider.GetUtcNow();
        var rotated = CreateSecret("afc");
        node.AgentCredentialHash = HashSecret(rotated);
        node.AgentCredentialIssuedAtUtc = now;
        node.LastSeenAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return new FarmAgentCredentialRotation(node.Id, rotated, now);
    }

    public async Task<FarmRemoteJobOffer?> ClaimJobAsync(
        string credential,
        CancellationToken cancellationToken = default,
        IReadOnlyList<Guid>? activeJobIds = null)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await AuthenticateNodeAsync(db, credential, cancellationToken);
        if (node is null ||
            node.Status != FarmNodeStatus.Online.ToString() ||
            !node.AgentAcceptJobs ||
            node.ConfirmedAtUtc is null)
            return null;
        await FarmJobLifecycle.ExpireAsync(
            db,
            timeProvider.GetUtcNow(),
            cancellationToken,
            nodeId: node.Id);

        var reportedActiveIds = NormalizeActiveJobIds(null, activeJobIds);
        var jobId = await db.FarmJobs
            .Where(x => x.NodeId == node.Id &&
                        (x.Status == "Offered" || x.Status == "Accepted" || x.Status == "Running") &&
                        !reportedActiveIds.Contains(x.Id))
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        jobId ??= await TryTopUpContinuousJobAsync(db, node, cancellationToken);
        if (jobId is null) return null;

        var accepted = await db.FarmJobs
            .Where(x => x.Id == jobId && x.Status == "Offered")
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.Status, FarmJobStatus.Accepted.ToString()),
                cancellationToken);
        var job = await db.FarmJobs.SingleAsync(x => x.Id == jobId, cancellationToken);
        if (job.Status is not ("Offered" or "Accepted" or "Running"))
            return null;
        if (!TryReadRemoteManifest(job, out var manifest) || manifest is null)
        {
            await FarmJobLifecycle.TryTerminateAsync(
                db,
                job.Id,
                FarmJobStatus.Rejected,
                "Manifesto remoto inválido.",
                timeProvider.GetUtcNow(),
                cancellationToken);
            return null;
        }
        if (manifest.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            await FarmJobLifecycle.TryTerminateAsync(
                db,
                job.Id,
                FarmJobStatus.TimedOut,
                "Manifesto remoto expirado antes do aceite.",
                timeProvider.GetUtcNow(),
                cancellationToken);
            return null;
        }
        if (accepted == 1)
        {
            await AddJobEventAsync(
                db,
                job,
                FarmJobStatus.Accepted,
                "Compute Agent aceitou a oferta assinada.",
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return new FarmRemoteJobOffer(
            job.Id,
            manifest.AttemptId,
            job.ManifestPayload!,
            job.ManifestDigest!,
            job.ManifestSignature!,
            SignatureAlgorithm,
            job.InputVersion is null ? null : new FarmRemoteJobInput(
                job.InputVersion,
                "text/plain; charset=utf-8",
                manifest.DataClassification,
                job.InputPayload!,
                job.InputSizeBytes!.Value,
                job.InputDigest!));
    }

    public async Task<bool> MarkJobStartedAsync(
        string credential,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await AuthenticateNodeAsync(db, credential, cancellationToken);
        if (node is null) return false;
        var now = timeProvider.GetUtcNow();
        var updated = await db.FarmJobs
            .Where(x => x.Id == jobId && x.NodeId == node.Id &&
                        x.Status == "Accepted" && x.ManifestExpiresAtUtc > now &&
                        x.CancellationRequestedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, FarmJobStatus.Running.ToString())
                    .SetProperty(x => x.StartedAtUtc, now)
                    .SetProperty(x => x.AgentLeaseRenewedAtUtc, now)
                    .SetProperty(x => x.AgentLeaseExpiresAtUtc, now.Add(AgentLeaseLifetime)),
                cancellationToken);
        if (updated == 1)
        {
            var job = await db.FarmJobs.SingleAsync(x => x.Id == jobId, cancellationToken);
            await AddJobEventAsync(
                db,
                job,
                FarmJobStatus.Running,
                "Agent validou o manifesto e iniciou o runtime autorizado.",
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var expired = await db.FarmJobs.SingleOrDefaultAsync(
            x => x.Id == jobId && x.NodeId == node.Id &&
                 x.Status == FarmJobStatus.Accepted.ToString() &&
                 x.ManifestExpiresAtUtc <= now,
            cancellationToken);
        if (expired is not null)
        {
            await FarmJobLifecycle.TryTerminateAsync(
                db,
                expired.Id,
                FarmJobStatus.TimedOut,
                "O manifesto expirou antes do início do runtime.",
                timeProvider.GetUtcNow(),
                cancellationToken);
            return false;
        }
        return await db.FarmJobs.AnyAsync(
            x => x.Id == jobId && x.NodeId == node.Id && x.Status == "Running",
            cancellationToken);
    }

    public async Task<bool> RejectJobAsync(
        string credential,
        Guid jobId,
        FarmRemoteJobRejection rejection,
        CancellationToken cancellationToken = default)
    {
        var reason = Required(rejection.Reason, nameof(rejection.Reason), 3, 200);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await AuthenticateNodeAsync(db, credential, cancellationToken);
        if (node is null) return false;
        var job = await db.FarmJobs.SingleOrDefaultAsync(
            x => x.Id == jobId && x.NodeId == node.Id &&
                 (x.Status == "Offered" || x.Status == "Accepted" || x.Status == "Running"),
            cancellationToken);
        if (job is null) return false;
        var status = rejection.Kind switch
        {
            FarmRemoteJobRejectionKind.Timeout => FarmJobStatus.TimedOut,
            FarmRemoteJobRejectionKind.Cancelled => FarmJobStatus.Cancelled,
            _ => FarmJobStatus.Rejected
        };
        return await FarmJobLifecycle.TryTerminateAsync(
            db,
            job.Id,
            status,
            $"Agent encerrou a oferta ({rejection.Kind}): {reason}",
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public async Task<FarmRemoteJobAcknowledgement?> SubmitResultAsync(
        string credential,
        Guid jobId,
        FarmRemoteJobResult result,
        CancellationToken cancellationToken = default)
    {
        if (result.DurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(result.DurationMilliseconds));
        if (result.PeakMemoryBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(result.PeakMemoryBytes));
        if (result.ProcessorTimeMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(result.ProcessorTimeMilliseconds));
        if (!FarmWorkerEnforcement.IsSupported(result.Enforcement))
            throw new ArgumentException(
                "O modo de contenção do worker não é reconhecido.",
                nameof(result.Enforcement));
        byte[] suppliedDigest;
        try { suppliedDigest = Convert.FromHexString(result.ResultDigest); }
        catch (FormatException exception)
        {
            throw new ArgumentException("ResultDigest deve ser hexadecimal.", nameof(result), exception);
        }
        if (suppliedDigest.Length != 32)
            throw new ArgumentException("ResultDigest deve possuir 256 bits.", nameof(result));

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await AuthenticateNodeAsync(db, credential, cancellationToken);
        if (node is null) return null;

        var existing = await db.FarmJobs.SingleOrDefaultAsync(
            x => x.Id == jobId && x.NodeId == node.Id,
            cancellationToken);
        if (existing is null) return null;
        if (existing.Status == FarmJobStatus.Completed.ToString())
        {
            if (!string.Equals(existing.ResultDigest, result.ResultDigest, StringComparison.OrdinalIgnoreCase) ||
                existing.DurationMilliseconds != result.DurationMilliseconds ||
                existing.WorkerPeakMemoryBytes != result.PeakMemoryBytes ||
                existing.WorkerProcessorTimeMilliseconds != result.ProcessorTimeMilliseconds ||
                existing.WorkerEnforcement != result.Enforcement ||
                existing.OutputPayload != result.OutputPayload)
                throw new InvalidOperationException("O Job já foi concluído com uma evidência diferente.");
            return new FarmRemoteJobAcknowledgement(
                existing.Id,
                FarmJobStatus.Completed,
                true,
                existing.ComputeUnitsMilli);
        }
        if (existing.CancellationRequestedAtUtc is not null)
        {
            return await TerminateAndAcknowledgeAsync(
                db,
                existing.Id,
                FarmJobStatus.Cancelled,
                "O Agent confirmou o cancelamento antes do envio da evidência.",
                cancellationToken);
        }
        if (FarmJobLifecycle.ActiveStatuses.Contains(existing.Status) &&
            existing.ManifestExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            return await TerminateAndAcknowledgeAsync(
                db,
                existing.Id,
                FarmJobStatus.TimedOut,
                "A evidência chegou após a validade do manifesto.",
                cancellationToken);
        }
        if (!TryReadRemoteManifest(existing, out var manifest) || manifest is null)
        {
            return await TerminateAndAcknowledgeAsync(
                db,
                existing.Id,
                FarmJobStatus.Rejected,
                "O manifesto remoto armazenado não é válido.",
                cancellationToken);
        }
        var definition = FarmWorkloadCatalog.Find(manifest.Workload, manifest.WorkloadVersion)
            ?? throw new InvalidOperationException("O workload remoto não pertence ao catálogo autorizado.");
        if (definition.OutputSchema is null && result.OutputPayload is not null)
            throw new ArgumentException("Este workload não produz saída tipada.", nameof(result));
        if (definition.OutputSchema is not null &&
            (string.IsNullOrWhiteSpace(result.OutputPayload) ||
             Encoding.UTF8.GetByteCount(result.OutputPayload) > 128 * 1024))
            throw new ArgumentException("A saída tipada é obrigatória e deve possuir até 128 KiB.", nameof(result));
        if (result.DurationMilliseconds > manifest.TimeoutSeconds * 1000L)
        {
            return await TerminateAndAcknowledgeAsync(
                db,
                existing.Id,
                FarmJobStatus.TimedOut,
                "A evidência excedeu o timeout assinado do Job.",
                cancellationToken);
        }
        var observedProfileHash = FarmNodeProfileFingerprint.Compute(
            node.Runtime,
            node.LogicalProcessors,
            node.MemoryBytes,
            node.AgentVersion);
        if (!string.Equals(manifest.NodeProfileHash, observedProfileHash, StringComparison.Ordinal))
        {
            return await TerminateAndAcknowledgeAsync(
                db,
                existing.Id,
                FarmJobStatus.Rejected,
                "O perfil observado do Node não corresponde ao perfil assinado no manifesto.",
                cancellationToken);
        }

        var claimed = await db.FarmJobs
            .Where(x => x.Id == jobId && x.NodeId == node.Id &&
                        x.CancellationRequestedAtUtc == null &&
                        (x.Status == "Accepted" || x.Status == "Running"))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, FarmJobStatus.Uploading.ToString())
                    .SetProperty(x => x.ResultDigest, result.ResultDigest.ToLowerInvariant())
                    .SetProperty(x => x.DurationMilliseconds, result.DurationMilliseconds)
                    .SetProperty(x => x.WorkerPeakMemoryBytes, result.PeakMemoryBytes)
                    .SetProperty(x => x.WorkerProcessorTimeMilliseconds, result.ProcessorTimeMilliseconds)
                    .SetProperty(x => x.WorkerEnforcement, result.Enforcement),
                cancellationToken);
        if (claimed != 1)
        {
            // Cerca contra a corrida upload × cancelamento: se o pedido de
            // cancelamento chegou depois da leitura inicial, a evidência é
            // descartada e o Job termina Cancelled, nunca Uploading.
            await db.Entry(existing).ReloadAsync(cancellationToken);
            if (existing.CancellationRequestedAtUtc is not null &&
                FarmJobLifecycle.ActiveStatuses.Contains(existing.Status))
            {
                return await TerminateAndAcknowledgeAsync(
                    db,
                    existing.Id,
                    FarmJobStatus.Cancelled,
                    "O cancelamento venceu a corrida com o upload; evidência parcial descartada.",
                    cancellationToken);
            }
            throw new InvalidOperationException("O Job não aceita evidência neste estado.");
        }

        await db.Entry(existing).ReloadAsync(cancellationToken);
        var job = existing;
        if (result.OutputPayload is not null)
        {
            job.OutputVersion = definition.OutputSchema;
            job.OutputPayload = result.OutputPayload;
            job.OutputSizeBytes = Encoding.UTF8.GetByteCount(result.OutputPayload);
            job.OutputDigest = result.ResultDigest.ToLowerInvariant();
        }
        await AddJobEventAsync(
            db,
            job,
            FarmJobStatus.Uploading,
            "Evidência SHA-256 recebida do Agent autenticado.",
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        // Geração da plataforma (ADR 0043): sem reexecução possível, o
        // resultado é aceito sem verificação e vale CP=0 até o quórum.
        var unverifiedGeneration = string.Equals(
            definition.VerificationPolicy,
            FarmWorkloadCatalog.UnverifiedGenerationPolicy,
            StringComparison.Ordinal);

        job.Status = FarmJobStatus.Verifying.ToString();
        await AddJobEventAsync(
            db,
            job,
            FarmJobStatus.Verifying,
            unverifiedGeneration
                ? "Geração aceita sem reexecução (UNVERIFIED_GENERATION_V1)."
                : "Servidor iniciou reexecução determinística independente.",
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        bool verified;
        if (unverifiedGeneration)
        {
            job.ExpectedDigest = result.ResultDigest.ToLowerInvariant();
            verified = result.OutputPayload is not null;
        }
        else
        {
            var expected = await Task.Run(
                () => FarmDeterministicWorkloads.Execute(
                    manifest.Workload,
                    manifest.Nonce,
                    manifest.Iterations,
                    job.InputPayload,
                    cancellationToken),
                cancellationToken);
            job.ExpectedDigest = expected.Digest;
            verified = CryptographicOperations.FixedTimeEquals(
                suppliedDigest,
                Convert.FromHexString(expected.Digest)) &&
                string.Equals(result.OutputPayload, expected.OutputPayload, StringComparison.Ordinal);
        }
        job.FinishedAtUtc = timeProvider.GetUtcNow();
        if (!verified)
        {
            job.Status = FarmJobStatus.Failed.ToString();
            job.FailureReason = "A evidência do Agent divergiu da reexecução do verificador.";
            await AddJobEventAsync(
                db,
                job,
                FarmJobStatus.Failed,
                "Digest divergente; nenhum CP ou AIC emitido.",
                cancellationToken);
        }
        else
        {
            job.Status = FarmJobStatus.Completed.ToString();
            job.ComputeUnitsMilli = unverifiedGeneration ? 0 : definition.ProcessingPointsMilli;
            if (!unverifiedGeneration)
            {
                node.VerificationLevel = FarmVerificationLevel.Verified.ToString();
                node.VerifiedProfileHash = manifest.NodeProfileHash;
                node.ProfileVerifiedAtUtc = job.FinishedAtUtc;
            }
            await AddJobEventAsync(
                db,
                job,
                FarmJobStatus.Completed,
                unverifiedGeneration
                    ? "Geração concluída pela rede; CP=0 até a verificação por quórum (ADR 0043)."
                    : $"{definition.ProcessingPointsMilli / 1_000m:N3} CP remoto verificado; AIC via resgate de recompensas.",
                cancellationToken,
                job.FinishedAtUtc.Value);
        }
        await db.SaveChangesAsync(cancellationToken);
        if (verified)
        {
            await FarmEvidenceSecurity.SealAsync(db, job, signingOptions, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            // Auto-proposta (ADR 0042) — só depois da evidência selada, que a
            // fotografia econômica exige íntegra.
            await rewardAutoProposer.TryProposeAsync(
                job.AccountId, job.Id, cancellationToken);
        }
        return new FarmRemoteJobAcknowledgement(
            job.Id,
            Enum.Parse<FarmJobStatus>(job.Status),
            verified,
            job.ComputeUnitsMilli);
    }

    private async Task<FarmRemoteJobAcknowledgement> TerminateAndAcknowledgeAsync(
        AIFarmDbContext db,
        Guid jobId,
        FarmJobStatus requestedStatus,
        string detail,
        CancellationToken cancellationToken)
    {
        await FarmJobLifecycle.TryTerminateAsync(
            db,
            jobId,
            requestedStatus,
            detail,
            timeProvider.GetUtcNow(),
            cancellationToken);
        var current = await db.FarmJobs
            .AsNoTracking()
            .SingleAsync(x => x.Id == jobId, cancellationToken);
        var status = Enum.Parse<FarmJobStatus>(current.Status);
        return new FarmRemoteJobAcknowledgement(
            current.Id,
            status,
            status == FarmJobStatus.Completed,
            current.ComputeUnitsMilli);
    }

    /// <summary>
    /// Farm contínua (ADR 0036): sem fila para o node, a conta autorizou o
    /// consumo e o binário está atual — nasce um job marathon pinado nele.
    /// O cooldown por node limita o custo da reexecução inline no servidor.
    /// </summary>
    private async Task<Guid?> TryTopUpContinuousJobAsync(
        AIFarmDbContext db,
        FarmNodeRecord node,
        CancellationToken cancellationToken)
    {
        // Binário antigo não conhece o workload marathon e rejeitaria o
        // próprio job, sujando a confiabilidade do node.
        if (!string.Equals(
                node.AgentVersion,
                FarmAgentProtocol.CurrentAgentVersion,
                StringComparison.Ordinal))
            return null;
        var enabled = await db.Accounts.AsNoTracking()
            .Where(x => x.Id == node.AccountId)
            .Select(x => x.ContinuousFarmEnabled)
            .SingleOrDefaultAsync(cancellationToken);
        if (!enabled) return null;

        var windowStartUtc = timeProvider.GetUtcNow()
            .AddSeconds(-continuousOptions.CooldownSeconds);
        var recentContinuousJobs = await db.FarmJobs.AsNoTracking()
            .CountAsync(
                x => x.NodeId == node.Id &&
                     x.WorkloadVersion == FarmWorkloadCatalog.ContinuousVersion &&
                     x.CreatedAtUtc > windowStartUtc,
                cancellationToken);
        if (recentContinuousJobs >= continuousOptions.MaxJobsPerWindow) return null;

        try
        {
            var snapshot = await continuousDispatcher.RunContinuousWorkloadAsync(
                node.AccountId, node.Id, cancellationToken);
            return snapshot.Id;
        }
        catch (InvalidOperationException)
        {
            // Sem capacidade residual, quarentena ou outro bloqueio do
            // seletor: o node simplesmente fica ocioso nesta rodada.
            return null;
        }
    }

    private async Task<FarmNodeRecord?> AuthenticateNodeAsync(
        AIFarmDbContext db,
        string credential,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential)) return null;
        var credentialHash = HashSecret(credential);
        return await db.FarmNodes.SingleOrDefaultAsync(
            x => x.AgentCredentialHash == credentialHash && x.ConnectionKind == "Agent",
            cancellationToken);
    }

    private bool TryReadRemoteManifest(FarmJobRecord job, out FarmJobManifest? manifest)
    {
        var key = signingOptions.DeriveForAgent(job.NodeId);
        if (!FarmManifestSecurity.TryVerify(
                job.ManifestPayload,
                job.ManifestDigest,
                job.ManifestSignature,
                key.Signing,
                out manifest) || manifest is null)
            return false;
        var definition = FarmWorkloadCatalog.Find(manifest.Workload, manifest.WorkloadVersion);
        return definition is not null &&
            manifest.Version == PostgresFarmLabService.RemoteManifestVersion &&
            manifest.JobId == job.Id &&
            manifest.AttemptId == job.AttemptId &&
            manifest.NodeId == job.NodeId &&
            manifest.Workload == job.Workload &&
            manifest.WorkloadVersion == job.WorkloadVersion &&
            manifest.VerificationPolicy == definition.VerificationPolicy &&
            manifest.Nonce == job.Nonce &&
            manifest.InputDigest == job.InputDigest &&
            SamePostgresTimestamp(manifest.IssuedAtUtc, job.ManifestIssuedAtUtc) &&
            SamePostgresTimestamp(manifest.ExpiresAtUtc, job.ManifestExpiresAtUtc) &&
            manifest.NetworkPolicy == definition.NetworkPolicy &&
            manifest.DataClassification == definition.DataClassification &&
            manifest.Iterations == definition.Iterations &&
            manifest.TimeoutSeconds == definition.TimeoutSeconds &&
            manifest.MaxLogicalProcessors == definition.MaxLogicalProcessors &&
            manifest.MaxMemoryBytes > 0 && manifest.MaxMemoryBytes <= definition.MaxMemoryBytes &&
            manifest.NodeProfileHash is { Length: 64 } &&
            InputMatchesDefinition(job, definition);
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
        catch (Exception exception) when (exception is ArgumentException or FormatException) { return false; }
    }

    private static bool SamePostgresTimestamp(DateTimeOffset left, DateTimeOffset? right) =>
        right is not null && Math.Abs((left - right.Value).Ticks) < 10;

    private async Task AddJobEventAsync(
        AIFarmDbContext db,
        FarmJobRecord job,
        FarmJobStatus status,
        string detail,
        CancellationToken cancellationToken,
        DateTimeOffset? occurredAtUtc = null)
    {
        var persisted = await db.FarmJobEvents
            .Where(x => x.JobId == job.Id)
            .MaxAsync(x => (int?)x.Sequence, cancellationToken) ?? 0;
        var tracked = db.ChangeTracker.Entries<FarmJobEventRecord>()
            .Count(x => x.State == EntityState.Added && x.Entity.JobId == job.Id);
        db.FarmJobEvents.Add(new FarmJobEventRecord
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            Sequence = persisted + tracked + 1,
            Status = status.ToString(),
            Detail = detail,
            OccurredAtUtc = occurredAtUtc ?? timeProvider.GetUtcNow()
        });
    }

    private static FarmAgentEnrollmentRequest Validate(FarmAgentEnrollmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var accountEmail = request.AccountEmail?.Trim() ?? string.Empty;
        if (accountEmail.Length > 320)
            throw new ArgumentException("E-mail da conta longo demais.", nameof(request.AccountEmail));
        return new FarmAgentEnrollmentRequest(
            Required(request.Name, nameof(request.Name), 3, 160),
            Required(request.Runtime, nameof(request.Runtime), 3, 500),
            Positive(request.LogicalProcessors, nameof(request.LogicalProcessors), 1024),
            Positive(request.MemoryBytes, nameof(request.MemoryBytes), 16L * 1024 * 1024 * 1024 * 1024),
            Required(request.AgentVersion, nameof(request.AgentVersion), 1, 80),
            accountEmail);
    }

    private static FarmAgentHeartbeatRequest Validate(FarmAgentHeartbeatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var logicalProcessors = Positive(request.LogicalProcessors, nameof(request.LogicalProcessors), 1024);
        var memoryBytes = Positive(
            request.MemoryBytes,
            nameof(request.MemoryBytes),
            16L * 1024 * 1024 * 1024 * 1024);
        var maxLogicalProcessors = request.MaxLogicalProcessors == 0
            ? Math.Min(1, logicalProcessors)
            : Positive(request.MaxLogicalProcessors, nameof(request.MaxLogicalProcessors), logicalProcessors);
        var maxMemoryBytes = request.MaxMemoryBytes == 0
            ? Math.Min(256L * 1024 * 1024, memoryBytes)
            : Positive(request.MaxMemoryBytes, nameof(request.MaxMemoryBytes), memoryBytes);
        return new FarmAgentHeartbeatRequest(
            Required(request.Runtime, nameof(request.Runtime), 3, 500),
            logicalProcessors,
            memoryBytes,
            Required(request.AgentVersion, nameof(request.AgentVersion), 1, 80),
            request.AcceptJobs,
            maxLogicalProcessors,
            maxMemoryBytes,
            request.ActiveJobId,
            NormalizeActiveJobIds(request.ActiveJobId, request.ActiveJobIds),
            request.WorkerCpuPercent,
            request.WorkerMemoryBytes,
            request.SupportsGeneration);
    }

    private static Guid[] NormalizeActiveJobIds(Guid? legacyJobId, IReadOnlyList<Guid>? jobIds)
    {
        var normalized = (jobIds ?? []).Append(legacyJobId ?? Guid.Empty)
            .Where(x => x != Guid.Empty).Distinct().ToArray();
        if (normalized.Length > 64)
            throw new ArgumentOutOfRangeException(nameof(jobIds), "O Agent pode reportar até 64 Jobs ativos.");
        return normalized;
    }

    private static string Required(string value, string name, int minimum, int maximum)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length < minimum || normalized.Length > maximum)
            throw new ArgumentException($"{name} deve possuir entre {minimum} e {maximum} caracteres.", name);
        return normalized;
    }

    private static T Positive<T>(T value, string name, T maximum)
        where T : struct, INumber<T>
    {
        if (value <= T.Zero || value > maximum)
            throw new ArgumentOutOfRangeException(name);
        return value;
    }

    private static string CreateSecret(string prefix)
    {
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{prefix}_{value}";
    }

    internal static string HashSecret(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
