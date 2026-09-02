using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresAuditTrail(
    IDbContextFactory<AIFarmDbContext> dbFactory,
    IAIFarmIdentityAccessor identityAccessor,
    FarmManifestSigningOptions signingOptions,
    TimeProvider timeProvider) : IAuditTrail
{
    private const int MaximumScan = 5_000;
    private const string ExportVersion = "atos-audit-export-v1";

    public async Task<AuditPage> SearchAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        var normalized = query.Normalize();
        var loaded = await LoadAsync(normalized, cancellationToken);
        var events = Filter(loaded.Events, normalized);
        var totals = Totals(events);
        var page = events
            .Skip((normalized.Page - 1) * normalized.PageSize)
            .Take(normalized.PageSize)
            .ToArray();
        return new AuditPage(page, totals, normalized.Page, normalized.PageSize, loaded.Truncated);
    }

    public async Task<AuditExport> ExportAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        var normalized = query.Normalize() with { Page = 1 };
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var loaded = await LoadAsync(normalized, cancellationToken);
        var events = Filter(loaded.Events, normalized);
        var generatedAt = timeProvider.GetUtcNow().ToUniversalTime();
        var payload = new ExportPayload(
            ExportVersion,
            identity.AccountId,
            generatedAt,
            normalized.Search,
            normalized.Category?.ToString(),
            normalized.FromUtc,
            normalized.ToUtc,
            loaded.Truncated,
            Totals(events),
            events);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson);
        var envelope = new ExportEnvelope(
            ExportVersion,
            Convert.ToHexStringLower(SHA256.HashData(payloadBytes)),
            payload);
        return new AuditExport(
            $"atos-audit-{generatedAt:yyyyMMdd-HHmmss}.json",
            "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(envelope, ExportJson));
    }

    private async Task<LoadedEvents> LoadAsync(
        AuditQuery query,
        CancellationToken cancellationToken)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var events = new List<AuditEventSnapshot>();
        var truncated = false;

        if (query.Category is null or AuditCategory.Ledger)
        {
            var records = await db.LedgerTransactions.AsNoTracking()
                .Where(x => x.WalletId == identity.WalletId)
                .Where(x => query.FromUtc == null || x.CreatedAtUtc >= query.FromUtc)
                .Where(x => query.ToUtc == null || x.CreatedAtUtc <= query.ToUtc)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(MaximumScan + 1)
                .ToArrayAsync(cancellationToken);
            truncated |= records.Length > MaximumScan;
            records = records.Take(MaximumScan).ToArray();
            var ids = records.Select(x => x.Id).ToArray();
            var entries = await db.LedgerEntries.AsNoTracking()
                .Where(x => ids.Contains(x.TransactionId))
                .ToArrayAsync(cancellationToken);
            var byTransaction = entries.GroupBy(x => x.TransactionId)
                .ToDictionary(x => x.Key, x => x.ToArray());
            events.AddRange(records.Select(record =>
            {
                var transactionEntries = byTransaction.GetValueOrDefault(record.Id, []);
                var debits = transactionEntries.Where(x => x.Direction == "Debit").Sum(x => x.AmountMinor);
                var credits = transactionEntries.Where(x => x.Direction == "Credit").Sum(x => x.AmountMinor);
                var integrity = transactionEntries.Length > 0 && debits == credits
                    ? AuditIntegrityStatus.Valid
                    : AuditIntegrityStatus.Invalid;
                return new AuditEventSnapshot(
                    record.Id, record.CreatedAtUtc, AuditCategory.Ledger,
                    record.Type.ToUpperInvariant(), record.SettledAtUtc is null ? "PENDING" : "SETTLED",
                    record.Description, Short(record.Id), Short(record.CorrelationId),
                    transactionEntries.Length == 0 ? null : transactionEntries.Max(x => x.AmountMinor),
                    null, integrity);
            }));
        }

        if (query.Category is null or AuditCategory.Iris)
        {
            var records = await (
                from execution in db.AiExecutions.AsNoTracking()
                join request in db.AiRequests.AsNoTracking() on execution.AiRequestId equals request.Id
                where request.AccountId == identity.AccountId &&
                      (query.FromUtc == null || execution.StartedAtUtc >= query.FromUtc) &&
                      (query.ToUtc == null || execution.StartedAtUtc <= query.ToUtc)
                orderby execution.StartedAtUtc descending
                select new { Execution = execution, Request = request })
                .Take(MaximumScan + 1)
                .ToArrayAsync(cancellationToken);
            truncated |= records.Length > MaximumScan;
            events.AddRange(records.Take(MaximumScan).Select(item => new AuditEventSnapshot(
                item.Execution.Id, item.Execution.StartedAtUtc, AuditCategory.Iris,
                "AI_EXECUTION", item.Execution.Status.ToUpperInvariant(),
                $"{item.Execution.Provider} // {item.Execution.Model} // {item.Request.DataClassification}",
                Short(item.Request.ConversationId), Short(item.Request.OperationId),
                item.Execution.ActualCostMinor, null, AuditIntegrityStatus.NotApplicable)));
        }

        if (query.Category is null or AuditCategory.Farm)
        {
            var records = await db.FarmJobs.AsNoTracking()
                .Where(x => x.AccountId == identity.AccountId)
                .Where(x => query.FromUtc == null || x.CreatedAtUtc >= query.FromUtc)
                .Where(x => query.ToUtc == null || x.CreatedAtUtc <= query.ToUtc)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(MaximumScan + 1)
                .ToArrayAsync(cancellationToken);
            truncated |= records.Length > MaximumScan;
            events.AddRange(records.Take(MaximumScan).Select(record =>
            {
                var integrity = record.EvidenceDigest is null
                    ? record.Status == "Completed" ? AuditIntegrityStatus.Invalid : AuditIntegrityStatus.NotApplicable
                    : FarmEvidenceSecurity.TryVerify(record, signingOptions, out _)
                        ? AuditIntegrityStatus.Valid : AuditIntegrityStatus.Invalid;
                return new AuditEventSnapshot(
                    record.Id, record.CreatedAtUtc, AuditCategory.Farm,
                    record.Workload.ToUpperInvariant(), record.Status.ToUpperInvariant(),
                    $"{record.WorkloadVersion} // {record.VerificationPolicy}",
                    Short(record.Id), record.AttemptId is null ? null : Short(record.AttemptId.Value),
                    record.ComputeUnitsMilli, record.EvidenceDigest, integrity);
            }));
        }

        if (query.Category is null or AuditCategory.Economics)
        {
            var records = await db.FarmEconomicsScenarios.AsNoTracking()
                .Where(x => x.AccountId == identity.AccountId)
                .Where(x => query.FromUtc == null || x.CreatedAtUtc >= query.FromUtc)
                .Where(x => query.ToUtc == null || x.CreatedAtUtc <= query.ToUtc)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(MaximumScan + 1)
                .ToArrayAsync(cancellationToken);
            truncated |= records.Length > MaximumScan;
            events.AddRange(records.Take(MaximumScan).Select(record =>
            {
                var snapshot = PostgresFarmEconomicsScenarioStore.ToSnapshot(record);
                return new AuditEventSnapshot(
                    record.Id, record.CreatedAtUtc, AuditCategory.Economics,
                    "SCENARIO_SNAPSHOT", snapshot.IsIntegrityValid ? "SEALED" : "TAMPERED",
                    record.Name, Short(record.Id), record.SourceJobId is null ? null : Short(record.SourceJobId.Value),
                    null, record.Digest,
                    snapshot.IsIntegrityValid ? AuditIntegrityStatus.Valid : AuditIntegrityStatus.Invalid);
            }));
        }

        if (query.Category is null or AuditCategory.Reward)
        {
            var records = await db.FarmRewardProposals.AsNoTracking()
                .Where(x => x.AccountId == identity.AccountId)
                .Where(x => query.FromUtc == null || x.CreatedAtUtc >= query.FromUtc)
                .Where(x => query.ToUtc == null || x.CreatedAtUtc <= query.ToUtc)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(MaximumScan + 1)
                .ToArrayAsync(cancellationToken);
            truncated |= records.Length > MaximumScan;
            events.AddRange(records.Take(MaximumScan).Select(record =>
            {
                var snapshot = PostgresFarmRewardProposalStore.ToSnapshot(record);
                return new AuditEventSnapshot(
                    record.Id, record.CreatedAtUtc, AuditCategory.Reward,
                    "REWARD_PROPOSAL", record.Status.ToUpperInvariant(),
                    $"R$ {record.RecommendedRewardBrl:N6} // {record.ProcessingPointsMilli / 1_000m:N3} CP",
                    Short(record.Id), Short(record.JobId), null, record.Digest,
                    snapshot.IsIntegrityValid ? AuditIntegrityStatus.Valid : AuditIntegrityStatus.Invalid);
            }));
        }

        return new LoadedEvents(events.OrderByDescending(x => x.OccurredAtUtc).ToArray(), truncated);
    }

    private static IReadOnlyList<AuditEventSnapshot> Filter(
        IReadOnlyList<AuditEventSnapshot> events,
        AuditQuery query)
    {
        if (query.Search is null) return events;
        return events.Where(x =>
            x.Action.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
            x.Status.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
            x.Description.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
            x.Reference.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
            (x.Correlation?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (x.Digest?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToArray();
    }

    private static AuditTotals Totals(IReadOnlyList<AuditEventSnapshot> events) => new(
        events.Count,
        events.Count(x => x.Category == AuditCategory.Ledger),
        events.Count(x => x.Category == AuditCategory.Iris),
        events.Count(x => x.Category == AuditCategory.Farm),
        events.Count(x => x.Category == AuditCategory.Economics),
        events.Count(x => x.Category == AuditCategory.Reward),
        events.Count(x => x.Integrity == AuditIntegrityStatus.Invalid));

    private static string Short(Guid value) => value.ToString("N")[..8];

    private sealed record LoadedEvents(IReadOnlyList<AuditEventSnapshot> Events, bool Truncated);
    private sealed record ExportPayload(
        string Version, Guid AccountId, DateTimeOffset GeneratedAtUtc,
        string? Search, string? Category, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc,
        bool IsScanTruncated, AuditTotals Totals, IReadOnlyList<AuditEventSnapshot> Events);
    private sealed record ExportEnvelope(string Version, string PayloadDigest, ExportPayload Payload);

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly JsonSerializerOptions ExportJson = new(CanonicalJson) { WriteIndented = true };
}
