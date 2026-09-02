using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresFarmRewardProposalStore(
    IDbContextFactory<AIFarmDbContext> dbFactory,
    IAIFarmIdentityAccessor identityAccessor,
    FarmManifestSigningOptions signingOptions,
    TimeProvider timeProvider) : IFarmRewardProposalStore
{
    public const string CurrentVersion = "atos-farm-reward-proposal-v1";

    public async Task<IReadOnlyList<FarmRewardProposalSnapshot>> ListAsync(
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var records = await db.FarmRewardProposals
            .AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 50))
            .ToArrayAsync(cancellationToken);
        return records.Select(ToSnapshot).ToArray();
    }

    public async Task<FarmRewardProposalSnapshot> CreateAsync(
        Guid jobId,
        Guid scenarioId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.FarmRewardProposals.AsNoTracking().SingleOrDefaultAsync(
            x => x.AccountId == identity.AccountId && x.ScenarioId == scenarioId,
            cancellationToken);
        if (existing is not null) return ToSnapshot(existing);

        var job = await db.FarmJobs.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == jobId && x.AccountId == identity.AccountId,
            cancellationToken) ?? throw new InvalidOperationException("O Job não pertence à conta.");
        if (job.Status != FarmJobStatus.Completed.ToString() ||
            job.ComputeUnitsMilli <= 0 ||
            !FarmEvidenceSecurity.TryVerify(job, signingOptions, out _))
            throw new InvalidOperationException("O Job precisa estar concluído e possuir evidência válida.");

        var scenarioRecord = await db.FarmEconomicsScenarios.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == scenarioId && x.AccountId == identity.AccountId,
            cancellationToken) ?? throw new InvalidOperationException("O cenário econômico não pertence à conta.");
        var scenario = PostgresFarmEconomicsScenarioStore.ToSnapshot(scenarioRecord);
        if (!scenario.IsIntegrityValid)
            throw new InvalidOperationException("O cenário econômico falhou na verificação de integridade.");
        if (scenario.SourceJobId != job.Id ||
            scenario.SourceEvidenceDigest != job.EvidenceDigest)
            throw new InvalidOperationException("O cenário deve ter sido medido a partir deste Job e de sua evidência atual.");

        var recommendedReward = decimal.Round(
            job.ComputeUnitsMilli * scenario.Result.MaximumRewardPerThousandCu / 1_000_000m,
            6,
            MidpointRounding.AwayFromZero);
        var record = new FarmRewardProposalRecord
        {
            Id = Guid.NewGuid(),
            AccountId = identity.AccountId,
            JobId = job.Id,
            ScenarioId = scenario.Id,
            ProcessingPointsMilli = job.ComputeUnitsMilli,
            RecommendedRewardBrl = recommendedReward,
            SourceEvidenceDigest = job.EvidenceDigest!,
            ScenarioDigest = scenario.Digest,
            Version = CurrentVersion,
            Status = FarmRewardProposalStatus.AwaitingPolicyAndFunding.ToString(),
            CreatedAtUtc = NormalizeTimestamp(timeProvider.GetUtcNow())
        };
        record.Digest = ComputeDigest(record);
        db.FarmRewardProposals.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return ToSnapshot(record);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var concurrent = await db.FarmRewardProposals.AsNoTracking().SingleOrDefaultAsync(
                x => x.AccountId == identity.AccountId && x.ScenarioId == scenarioId,
                cancellationToken);
            if (concurrent is null) throw;
            return ToSnapshot(concurrent);
        }
    }

    internal static FarmRewardProposalSnapshot ToSnapshot(FarmRewardProposalRecord record)
    {
        var expected = ComputeDigest(record);
        var valid = record.Digest.Length == expected.Length &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(record.Digest),
                Encoding.ASCII.GetBytes(expected));
        return new FarmRewardProposalSnapshot(
            record.Id,
            record.JobId,
            record.ScenarioId,
            record.ProcessingPointsMilli,
            record.RecommendedRewardBrl,
            record.SourceEvidenceDigest,
            record.ScenarioDigest,
            record.Version,
            Enum.Parse<FarmRewardProposalStatus>(record.Status),
            record.CreatedAtUtc,
            record.Digest,
            valid);
    }

    private static string ComputeDigest(FarmRewardProposalRecord record)
    {
        var material = string.Join('\n',
            record.Version,
            record.AccountId.ToString("N"),
            record.JobId.ToString("N"),
            record.ScenarioId.ToString("N"),
            record.ProcessingPointsMilli.ToString(CultureInfo.InvariantCulture),
            record.RecommendedRewardBrl.ToString("0.000000", CultureInfo.InvariantCulture),
            record.SourceEvidenceDigest,
            record.ScenarioDigest,
            record.Status,
            record.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % 10));
    }
}
