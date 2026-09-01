using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

internal sealed class PostgresFarmLabService(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    TimeProvider timeProvider,
    FarmManifestSigningOptions signingOptions) : IFarmLabService
{
    private const int BenchmarkIterations = 100_000;
    private const int BenchmarkTimeoutSeconds = 30;
    private const long BenchmarkMemoryLimitBytes = 256L * 1024 * 1024;
    private const string ManifestVersion = "atos-farm-manifest-v1";
    internal const string RemoteManifestVersion = "atos-farm-manifest-v2-agent";
    private const string Workload = "SHA256_CHAIN";
    private const string WorkloadVersion = "atos-sha256-chain-v1";
    private const string VerificationPolicy = "DETERMINISTIC_REEXECUTION_V1";
    private const string NetworkPolicy = "DENY_ALL";
    private const string DataClassification = "STANDARD";
    private static readonly TimeSpan AgentHeartbeatTolerance = TimeSpan.FromSeconds(90);

    public async Task<FarmDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await EnsureNodeAsync(db, identity.AccountId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        await FarmJobLifecycle.ExpireAsync(
            db,
            now,
            cancellationToken,
            accountId: identity.AccountId);
        if (node.ConnectionKind == FarmNodeConnectionKind.Embedded.ToString())
        {
            node.LastSeenAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
        }
        return await BuildDashboardAsync(db, node, cancellationToken);
    }

    public async Task<FarmDashboard> SetNodeStatusAsync(
        FarmNodeStatus status,
        CancellationToken cancellationToken = default)
    {
        if (status is not (FarmNodeStatus.Online or FarmNodeStatus.Paused))
            throw new ArgumentOutOfRangeException(nameof(status));

        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await EnsureNodeAsync(db, identity.AccountId, cancellationToken);
        node.Status = status.ToString();
        if (node.ConnectionKind == FarmNodeConnectionKind.Embedded.ToString())
            node.LastSeenAtUtc = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return await BuildDashboardAsync(db, node, cancellationToken);
    }

    public async Task<FarmJobSnapshot> RunBenchmarkAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await EnsureNodeAsync(db, identity.AccountId, cancellationToken);
        await FarmJobLifecycle.ExpireAsync(
            db,
            timeProvider.GetUtcNow(),
            cancellationToken,
            accountId: identity.AccountId);
        var isAgent = node.ConnectionKind == FarmNodeConnectionKind.Agent.ToString();
        if (!Enum.TryParse<FarmNodeStatus>(node.Status, out var nodeStatus) ||
            nodeStatus != FarmNodeStatus.Online)
            throw new InvalidOperationException("Retome o node antes de executar um benchmark.");
        if (isAgent && timeProvider.GetUtcNow() - node.LastSeenAtUtc > AgentHeartbeatTolerance)
            throw new InvalidOperationException("O Compute Agent está offline; aguarde um heartbeat válido.");
        if (isAgent && !node.AgentAcceptJobs)
            throw new InvalidOperationException("A política local do Compute Agent está pausada.");

        if (await db.FarmJobs.AnyAsync(
                x => x.AccountId == identity.AccountId &&
                     FarmJobLifecycle.ActiveStatuses.Contains(x.Status),
                cancellationToken))
            throw new InvalidOperationException("Já existe um Job do laboratório em execução.");

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
                Workload,
                WorkloadVersion,
                VerificationPolicy,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                BenchmarkIterations,
                BenchmarkTimeoutSeconds,
                1,
                Math.Min(
                    Math.Min(node.MemoryBytes, BenchmarkMemoryLimitBytes),
                    node.AgentMaxMemoryBytes ?? node.MemoryBytes),
                NetworkPolicy,
                DataClassification,
                now,
                now.AddMinutes(5)),
            manifestSigning);
        var job = new FarmJobRecord
        {
            Id = jobId,
            AccountId = identity.AccountId,
            NodeId = node.Id,
            Workload = Workload,
            WorkloadVersion = WorkloadVersion,
            VerificationPolicy = VerificationPolicy,
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
        AddEvent(db, job, FarmJobStatus.Queued, now, "Manifest fixo criado pelo scheduler do laboratório.");
        if (isAgent)
        {
            job.Status = FarmJobStatus.Offered.ToString();
            AddEvent(db, job, FarmJobStatus.Offered, now, "Job assinado oferecido ao Compute Agent vinculado.");
        }
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception)
        {
            throw new InvalidOperationException(
                "Não foi possível enfileirar o Job; pode existir outra execução ativa.",
                exception);
        }

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
            var resultDigest = await Task.Run(
                () => DeterministicBenchmark.Compute(
                    manifest.Nonce,
                    manifest.Iterations,
                    timeout.Token),
                timeout.Token);
            stopwatch.Stop();

            Transition(db, job, FarmJobStatus.Verifying, "Resultado recebido; reexecução determinística iniciada.");
            job.DurationMilliseconds = Math.Max(1, stopwatch.ElapsedMilliseconds);
            job.ResultDigest = resultDigest;
            await db.SaveChangesAsync(cancellationToken);

            var expectedDigest = await Task.Run(
                () => DeterministicBenchmark.Compute(
                    manifest.Nonce,
                    manifest.Iterations,
                    timeout.Token),
                timeout.Token);
            job.ExpectedDigest = expectedDigest;
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(resultDigest),
                    Convert.FromHexString(expectedDigest)))
                throw new InvalidOperationException("O resultado do benchmark não passou na verificação.");

            job.Status = FarmJobStatus.Completed.ToString();
            job.ComputeUnitsMilli = 1_000;
            job.FinishedAtUtc = timeProvider.GetUtcNow();
            node.VerificationLevel = FarmVerificationLevel.Verified.ToString();
            node.LastSeenAtUtc = job.FinishedAtUtc.Value;
            AddEvent(db, job, FarmJobStatus.Completed, job.FinishedAtUtc.Value, "1,000 CU verificado; nenhum AIC emitido.");
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await MarkTerminalAsync(
                db,
                job,
                FarmJobStatus.TimedOut,
                "O benchmark excedeu o timeout do manifesto.");
        }
        catch (OperationCanceledException)
        {
            await MarkTerminalAsync(
                db,
                job,
                FarmJobStatus.Cancelled,
                "A execução do benchmark foi cancelada.");
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkTerminalAsync(db, job, FarmJobStatus.Failed, exception.Message);
        }

        return await LoadJobAsync(db, job.Id, cancellationToken);
    }

    public async Task<FarmDashboard> CancelJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await EnsureNodeAsync(db, identity.AccountId, cancellationToken);
        if (node.ConnectionKind != FarmNodeConnectionKind.Agent.ToString())
            throw new InvalidOperationException("Cancelamento remoto está disponível apenas para Jobs do Compute Agent.");
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
        if (job.Status == FarmJobStatus.Cancelled.ToString())
            return await BuildDashboardAsync(db, node, cancellationToken);
        if (job.Status is not ("Offered" or "Accepted" or "Running"))
            throw new InvalidOperationException("Este Job não pode mais ser cancelado.");

        var cancelled = await FarmJobLifecycle.TryTerminateAsync(
            db,
            job.Id,
            FarmJobStatus.Cancelled,
            "Cancelado pelo usuário; nenhum CU ou AIC emitido.",
            timeProvider.GetUtcNow(),
            cancellationToken);
        if (!cancelled)
            throw new InvalidOperationException("O estado do Job mudou antes do cancelamento.");
        return await BuildDashboardAsync(db, node, cancellationToken);
    }

    private async Task<FarmNodeRecord> EnsureNodeAsync(
        AIFarmDbContext db,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var node = await db.FarmNodes.SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
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

    private async Task<FarmDashboard> BuildDashboardAsync(
        AIFarmDbContext db,
        FarmNodeRecord node,
        CancellationToken cancellationToken)
    {
        var jobs = await db.FarmJobs
            .Where(x => x.AccountId == node.AccountId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(10)
            .ToArrayAsync(cancellationToken);
        var ids = jobs.Select(x => x.Id).ToArray();
        var events = await db.FarmJobEvents
            .Where(x => ids.Contains(x.JobId))
            .OrderBy(x => x.Sequence)
            .ToArrayAsync(cancellationToken);
        return new FarmDashboard(ToSnapshot(node), jobs.Select(
            x => ToSnapshot(x, events.Where(e => e.JobId == x.Id))).ToArray());
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
        if (manifest.Iterations != BenchmarkIterations ||
            manifest.TimeoutSeconds != BenchmarkTimeoutSeconds ||
            manifest.MaxLogicalProcessors != 1 ||
            manifest.MaxMemoryBytes < 0 ||
            manifest.MaxMemoryBytes > BenchmarkMemoryLimitBytes ||
            manifest.NetworkPolicy != NetworkPolicy ||
            manifest.DataClassification != DataClassification)
            throw new InvalidOperationException("O manifesto do Job viola a allowlist do laboratório.");
        return manifest;
    }

    private bool TryReadVerifiedManifest(
        FarmJobRecord job,
        out FarmJobManifest? manifest)
    {
        var verification = job.ManifestVersion == RemoteManifestVersion
            ? signingOptions.DeriveForAgent(job.NodeId).Signing
            : signingOptions;
        if (!FarmManifestSecurity.TryVerify(
                job.ManifestPayload,
                job.ManifestDigest,
                job.ManifestSignature,
                verification,
                out manifest) ||
            manifest is null)
            return false;
        return manifest.Version is ManifestVersion or RemoteManifestVersion &&
            manifest.JobId == job.Id &&
            manifest.AttemptId == job.AttemptId &&
            manifest.NodeId == job.NodeId &&
            manifest.Workload == job.Workload &&
            manifest.WorkloadVersion == job.WorkloadVersion &&
            manifest.VerificationPolicy == job.VerificationPolicy &&
            manifest.Nonce == job.Nonce &&
            manifest.IssuedAtUtc == job.ManifestIssuedAtUtc &&
            manifest.ExpiresAtUtc == job.ManifestExpiresAtUtc;
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
        AddEvent(db, job, status, job.FinishedAtUtc.Value, $"{reason} Nenhum CU ou AIC emitido.");
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

    private FarmNodeSnapshot ToSnapshot(FarmNodeRecord node)
    {
        var connectionKind = Enum.TryParse<FarmNodeConnectionKind>(node.ConnectionKind, out var parsedKind)
            ? parsedKind
            : FarmNodeConnectionKind.Embedded;
        var heartbeatFresh = connectionKind == FarmNodeConnectionKind.Embedded ||
            timeProvider.GetUtcNow() - node.LastSeenAtUtc <= AgentHeartbeatTolerance;
        var persistedStatus = Enum.Parse<FarmNodeStatus>(node.Status);
        var effectiveStatus = heartbeatFresh ? persistedStatus : FarmNodeStatus.Offline;
        var acceptJobs = effectiveStatus == FarmNodeStatus.Online &&
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
            node.AgentMaxMemoryBytes ?? node.MemoryBytes);
    }

    private FarmJobSnapshot ToSnapshot(
        FarmJobRecord job,
        IEnumerable<FarmJobEventRecord> events)
    {
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
                manifest.DataClassification);
        }

        return new FarmJobSnapshot(
            job.Id,
            job.NodeId,
            job.Workload,
            job.WorkloadVersion,
            job.VerificationPolicy,
            Enum.Parse<FarmJobStatus>(job.Status),
            job.ComputeUnitsMilli,
            job.ResultDigest,
            job.CreatedAtUtc,
            job.FinishedAtUtc,
            manifestSnapshot,
            events.Select(x => new FarmJobEventSnapshot(
                x.Sequence,
                Enum.Parse<FarmJobStatus>(x.Status),
                x.OccurredAtUtc,
                x.Detail)).ToArray());
    }
}

internal static class DeterministicBenchmark
{
    public static string Compute(
        string nonce,
        int iterations,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nonce)) throw new ArgumentException("Nonce obrigatório.", nameof(nonce));
        if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
        for (var index = 0; index < iterations; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            hash = SHA256.HashData(hash);
        }
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
