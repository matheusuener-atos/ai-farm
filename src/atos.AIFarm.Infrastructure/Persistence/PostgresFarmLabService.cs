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
    TimeProvider timeProvider) : IFarmLabService
{
    private const int BenchmarkIterations = 100_000;
    private const string Workload = "SHA256_CHAIN";
    private const string WorkloadVersion = "atos-sha256-chain-v1";
    private const string VerificationPolicy = "DETERMINISTIC_REEXECUTION_V1";

    public async Task<FarmDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await EnsureNodeAsync(db, identity.AccountId, cancellationToken);
        node.LastSeenAtUtc = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
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
        node.LastSeenAtUtc = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return await BuildDashboardAsync(db, node, cancellationToken);
    }

    public async Task<FarmJobSnapshot> RunBenchmarkAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await EnsureNodeAsync(db, identity.AccountId, cancellationToken);
        if (!Enum.TryParse<FarmNodeStatus>(node.Status, out var nodeStatus) ||
            nodeStatus != FarmNodeStatus.Online)
            throw new InvalidOperationException("Retome o node antes de executar um benchmark.");

        if (await db.FarmJobs.AnyAsync(
                x => x.AccountId == identity.AccountId &&
                     (x.Status == "Queued" || x.Status == "Running" || x.Status == "Verifying"),
                cancellationToken))
            throw new InvalidOperationException("Já existe um Job do laboratório em execução.");

        var now = timeProvider.GetUtcNow();
        var job = new FarmJobRecord
        {
            Id = Guid.NewGuid(),
            AccountId = identity.AccountId,
            NodeId = node.Id,
            Workload = Workload,
            WorkloadVersion = WorkloadVersion,
            VerificationPolicy = VerificationPolicy,
            Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            Status = FarmJobStatus.Queued.ToString(),
            CreatedAtUtc = now
        };
        db.FarmJobs.Add(job);
        AddEvent(db, job, FarmJobStatus.Queued, now, "Manifest fixo aceito pelo laboratório.");
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            Transition(db, job, FarmJobStatus.Running, "Runtime autorizado iniciou o workload.");
            job.StartedAtUtc = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);

            var stopwatch = Stopwatch.StartNew();
            var resultDigest = DeterministicBenchmark.Compute(job.Nonce, BenchmarkIterations);
            stopwatch.Stop();

            Transition(db, job, FarmJobStatus.Verifying, "Resultado recebido; reexecução determinística iniciada.");
            job.DurationMilliseconds = Math.Max(1, stopwatch.ElapsedMilliseconds);
            job.ResultDigest = resultDigest;
            await db.SaveChangesAsync(cancellationToken);

            var expectedDigest = DeterministicBenchmark.Compute(job.Nonce, BenchmarkIterations);
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            job.Status = FarmJobStatus.Failed.ToString();
            job.FailureReason = exception.Message;
            job.FinishedAtUtc = timeProvider.GetUtcNow();
            AddEvent(db, job, FarmJobStatus.Failed, job.FinishedAtUtc.Value, "Falha registrada sem reward.");
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return await LoadJobAsync(db, job.Id, cancellationToken);
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

    private static async Task<FarmJobSnapshot> LoadJobAsync(
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

    private static void Transition(
        AIFarmDbContext db,
        FarmJobRecord job,
        FarmJobStatus status,
        string detail)
    {
        job.Status = status.ToString();
        AddEvent(db, job, status, DateTimeOffset.UtcNow, detail);
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

    private static FarmNodeSnapshot ToSnapshot(FarmNodeRecord node) => new(
        node.Id,
        node.Name,
        Enum.Parse<FarmNodeStatus>(node.Status),
        Enum.Parse<FarmVerificationLevel>(node.VerificationLevel),
        node.Runtime,
        node.LogicalProcessors,
        node.MemoryBytes,
        node.LastSeenAtUtc);

    private static FarmJobSnapshot ToSnapshot(
        FarmJobRecord job,
        IEnumerable<FarmJobEventRecord> events) => new(
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
        events.Select(x => new FarmJobEventSnapshot(
            x.Sequence,
            Enum.Parse<FarmJobStatus>(x.Status),
            x.OccurredAtUtc,
            x.Detail)).ToArray());
}

internal static class DeterministicBenchmark
{
    public static string Compute(string nonce, int iterations)
    {
        if (string.IsNullOrWhiteSpace(nonce)) throw new ArgumentException("Nonce obrigatório.", nameof(nonce));
        if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
        for (var index = 0; index < iterations; index++)
            hash = SHA256.HashData(hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
