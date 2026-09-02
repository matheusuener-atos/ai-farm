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
}
