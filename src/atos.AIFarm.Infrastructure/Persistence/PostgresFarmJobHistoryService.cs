using System.Security.Cryptography;
using System.Text.Json;
using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

internal sealed class PostgresFarmJobHistoryService(
    IDbContextFactory<AIFarmDbContext> dbFactory,
    IAIFarmIdentityAccessor identityAccessor,
    PostgresFarmLabService farmLab,
    TimeProvider timeProvider) : IFarmJobHistoryService
{
    private const int MaximumScan = 5_000;
    private const string ExportVersion = "atos-farm-job-history-export-v1";

    public async Task<FarmJobHistoryPage> SearchAsync(
        FarmJobHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var normalized = query.Normalize();
        var loaded = await LoadAsync(normalized, cancellationToken);
        var page = loaded.Items
            .Skip((normalized.Page - 1) * normalized.PageSize)
            .Take(normalized.PageSize)
            .ToArray();
        return new FarmJobHistoryPage(
            page,
            Totals(loaded.Items),
            normalized.Page,
            normalized.PageSize,
            loaded.Truncated);
    }

    public async Task<FarmJobHistoryExport> ExportAsync(
        FarmJobHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var normalized = query.Normalize() with { Page = 1 };
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var loaded = await LoadAsync(normalized, cancellationToken);
        var generatedAt = timeProvider.GetUtcNow().ToUniversalTime();
        var payload = new ExportPayload(
            ExportVersion,
            identity.AccountId,
            generatedAt,
            normalized,
            Totals(loaded.Items),
            loaded.Truncated,
            loaded.Items);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson);
        var envelope = new ExportEnvelope(
            Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant(),
            payload);
        return new FarmJobHistoryExport(
            $"atos-farm-jobs-{generatedAt:yyyyMMdd-HHmmss}.json",
            "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(envelope, ExportJson));
    }

    private async Task<LoadedJobs> LoadAsync(
        FarmJobHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var recordsQuery = db.FarmJobs.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId);

        if (query.Status is not null)
            recordsQuery = recordsQuery.Where(x => x.Status == query.Status.Value.ToString());
        if (query.Origin == FarmJobOrigin.PersonalApi)
            recordsQuery = recordsQuery.Where(x => x.ApiTokenId != null);
        else if (query.Origin == FarmJobOrigin.Interactive)
            recordsQuery = recordsQuery.Where(x => x.ApiTokenId == null);
        if (query.FromUtc is not null)
            recordsQuery = recordsQuery.Where(x => x.CreatedAtUtc >= query.FromUtc);
        if (query.ToUtc is not null)
            recordsQuery = recordsQuery.Where(x => x.CreatedAtUtc <= query.ToUtc);
        if (query.WorkloadId is not null)
        {
            var workload = FarmWorkloadCatalog.GetRequired(query.WorkloadId);
            recordsQuery = recordsQuery.Where(x =>
                x.Workload == workload.Workload && x.WorkloadVersion == workload.Version);
        }
        if (query.Search is not null)
        {
            if (Guid.TryParse(query.Search, out var jobId))
                recordsQuery = recordsQuery.Where(x => x.Id == jobId || x.AttemptId == jobId);
            else
            {
                var pattern = $"%{EscapeLike(query.Search)}%";
                recordsQuery = recordsQuery.Where(x =>
                    EF.Functions.ILike(x.Workload, pattern, "\\") ||
                    EF.Functions.ILike(x.WorkloadVersion, pattern, "\\") ||
                    EF.Functions.ILike(x.VerificationPolicy, pattern, "\\"));
            }
        }

        var records = await recordsQuery
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(MaximumScan + 1)
            .ToArrayAsync(cancellationToken);
        var truncated = records.Length > MaximumScan;
        records = records.Take(MaximumScan).ToArray();
        var ids = records.Select(x => x.Id).ToArray();
        var events = await db.FarmJobEvents.AsNoTracking()
            .Where(x => ids.Contains(x.JobId))
            .OrderBy(x => x.Sequence)
            .ToArrayAsync(cancellationToken);
        var eventsByJob = events.GroupBy(x => x.JobId)
            .ToDictionary(x => x.Key, x => (IEnumerable<FarmJobEventRecord>)x.ToArray());
        var items = records.Select(record => new FarmJobHistoryItem(
                farmLab.ToSnapshot(
                    record,
                    eventsByJob.GetValueOrDefault(record.Id) ?? []),
                record.ApiTokenId is null
                    ? FarmJobOrigin.Interactive
                    : FarmJobOrigin.PersonalApi))
            .Where(x => !query.IntegrityFailuresOnly ||
                x.Job.Status == FarmJobStatus.Completed && x.Job.Evidence?.IsValid != true)
            .ToArray();
        return new LoadedJobs(items, truncated);
    }

    private static FarmJobHistoryTotals Totals(IReadOnlyCollection<FarmJobHistoryItem> items)
    {
        var verified = items.Where(x =>
            x.Job.Status == FarmJobStatus.Completed && x.Job.Evidence?.IsValid == true).ToArray();
        return new FarmJobHistoryTotals(
            items.Count,
            items.Count(x => IsActive(x.Job.Status)),
            verified.Length,
            items.Count(x => IsFailed(x.Job.Status)),
            items.Count(x => x.Job.Status == FarmJobStatus.Completed && x.Job.Evidence?.IsValid != true),
            verified.Sum(x => x.Job.ComputeUnitsMilli));
    }

    private static bool IsActive(FarmJobStatus status) => status is
        FarmJobStatus.Queued or FarmJobStatus.Offered or FarmJobStatus.Accepted or
        FarmJobStatus.Running or FarmJobStatus.Uploading or FarmJobStatus.Verifying;

    private static bool IsFailed(FarmJobStatus status) => status is
        FarmJobStatus.Failed or FarmJobStatus.Cancelled or
        FarmJobStatus.TimedOut or FarmJobStatus.Rejected;

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private sealed record LoadedJobs(
        IReadOnlyList<FarmJobHistoryItem> Items,
        bool Truncated);

    private sealed record ExportPayload(
        string Version,
        Guid AccountId,
        DateTimeOffset GeneratedAtUtc,
        FarmJobHistoryQuery Query,
        FarmJobHistoryTotals Totals,
        bool IsScanTruncated,
        IReadOnlyList<FarmJobHistoryItem> Jobs);

    private sealed record ExportEnvelope(
        string PayloadDigest,
        ExportPayload Payload);

    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ExportJson = new(CanonicalJson) { WriteIndented = true };
}
