using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresFarmEconomicsScenarioStore(
    IDbContextFactory<AIFarmDbContext> dbFactory,
    IAIFarmIdentityAccessor identityAccessor,
    TimeProvider timeProvider) : IFarmEconomicsScenarioStore
{
    public const string CurrentVersion = "atos-farm-economics-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<FarmEconomicsScenarioSnapshot>> ListAsync(
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var records = await db.FarmEconomicsScenarios
            .AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 50))
            .ToListAsync(cancellationToken);

        return records.Select(ToSnapshot).ToArray();
    }

    public async Task<FarmEconomicsScenarioSnapshot> SaveAsync(
        string name,
        FarmEconomicsScenario scenario,
        Guid? sourceJobId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        var result = FarmEconomicsCalculator.Calculate(scenario);
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        string? sourceEvidenceDigest = null;
        if (sourceJobId is not null)
        {
            var source = await db.FarmJobs.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == sourceJobId &&
                     x.AccountId == identity.AccountId &&
                     x.Status == FarmJobStatus.Completed.ToString(),
                cancellationToken);
            if (source is null)
                throw new InvalidOperationException("O benchmark de origem não pertence à conta ou não foi concluído.");
            sourceEvidenceDigest = source.EvidenceDigest;
        }

        var inputPayload = JsonSerializer.Serialize(scenario, JsonOptions);
        var resultPayload = JsonSerializer.Serialize(result, JsonOptions);
        var createdAtUtc = NormalizeTimestamp(timeProvider.GetUtcNow());
        var record = new FarmEconomicsScenarioRecord
        {
            Id = Guid.NewGuid(),
            AccountId = identity.AccountId,
            Name = normalizedName,
            Version = CurrentVersion,
            InputPayload = inputPayload,
            ResultPayload = resultPayload,
            SourceJobId = sourceJobId,
            SourceEvidenceDigest = sourceEvidenceDigest,
            CreatedAtUtc = createdAtUtc
        };
        record.Digest = ComputeDigest(record);
        db.FarmEconomicsScenarios.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return ToSnapshot(record);
    }

    public async Task<FarmEconomicsReportDownload?> DownloadReportAsync(
        Guid scenarioId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.FarmEconomicsScenarios.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == scenarioId && x.AccountId == identity.AccountId,
            cancellationToken);
        if (record is null) return null;

        var snapshot = ToSnapshot(record);
        if (!snapshot.IsIntegrityValid)
            throw new InvalidOperationException("A fotografia econômica falhou na verificação de integridade.");

        var payload = new FarmEconomicsReportPayload(
            snapshot.Id,
            snapshot.Name,
            snapshot.Version,
            snapshot.CreatedAtUtc,
            snapshot.SourceJobId,
            snapshot.SourceEvidenceDigest,
            snapshot.Scenario,
            snapshot.Result,
            snapshot.Digest);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var payloadDigest = Convert.ToHexStringLower(SHA256.HashData(payloadBytes));
        var report = new FarmEconomicsReportEnvelope(
            "atos-farm-economics-report-v1",
            "SHA256",
            payloadDigest,
            payload);
        var content = JsonSerializer.SerializeToUtf8Bytes(
            report,
            new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
        return new FarmEconomicsReportDownload(
            $"atos-farm-economics-{scenarioId:N}.json",
            content);
    }

    private static FarmEconomicsScenarioSnapshot ToSnapshot(FarmEconomicsScenarioRecord record)
    {
        var scenario = JsonSerializer.Deserialize<FarmEconomicsScenario>(record.InputPayload, JsonOptions)
            ?? throw new InvalidOperationException("O cenário econômico persistido é inválido.");
        var result = JsonSerializer.Deserialize<FarmEconomicsResult>(record.ResultPayload, JsonOptions)
            ?? throw new InvalidOperationException("O resultado econômico persistido é inválido.");
        var expectedDigest = ComputeDigest(record);
        return new FarmEconomicsScenarioSnapshot(
            record.Id, record.Name, record.Version, scenario, result,
            record.SourceJobId, record.SourceEvidenceDigest, record.CreatedAtUtc,
            record.Digest,
            CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(record.Digest),
                Encoding.ASCII.GetBytes(expectedDigest)));
    }

    private static string ComputeDigest(FarmEconomicsScenarioRecord record)
    {
        var material = string.Join('\n',
            record.Version,
            record.AccountId.ToString("N"),
            record.Name,
            record.InputPayload,
            record.ResultPayload,
            record.SourceJobId?.ToString("N") ?? string.Empty,
            record.SourceEvidenceDigest ?? string.Empty,
            record.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % 10));
    }

    private static string NormalizeName(string value)
    {
        var normalized = string.Join(' ',
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length is < 1 or > 80)
            throw new ArgumentException("O nome do cenário deve ter entre 1 e 80 caracteres.", nameof(value));
        return normalized;
    }

    private sealed record FarmEconomicsReportPayload(
        Guid ScenarioId,
        string Name,
        string ScenarioVersion,
        DateTimeOffset CreatedAtUtc,
        Guid? SourceJobId,
        string? SourceEvidenceDigest,
        FarmEconomicsScenario Input,
        FarmEconomicsResult Result,
        string SnapshotDigest);

    private sealed record FarmEconomicsReportEnvelope(
        string Version,
        string Algorithm,
        string PayloadDigest,
        FarmEconomicsReportPayload Payload);
}
