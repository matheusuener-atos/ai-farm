using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresFarmKnowledgeSearchService(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor) : IFarmKnowledgeSearchService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<FarmKnowledgeSearchResult> SearchAsync(
        string query,
        int maximumHits = 4,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query?.Trim() ?? string.Empty;
        if (normalizedQuery.Length is < 2 or > 4_000)
            throw new ArgumentOutOfRangeException(nameof(query), "A busca deve conter entre 2 e 4.000 caracteres.");
        if (maximumHits is < 1 or > FarmKnowledgeRetrieval.MaximumHits)
            throw new ArgumentOutOfRangeException(nameof(maximumHits));

        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.FarmDemands.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId &&
                        x.WorkloadId == "iris-text-index-v1" &&
                        x.Status == FarmDemandStatus.Completed.ToString() &&
                        x.OutputPayload != null && x.OutputDigest != null)
            .OrderByDescending(x => x.FinishedAtUtc)
            .Take(100)
            .Select(x => new
            {
                x.Id,
                x.Label,
                x.InputPayload,
                x.OutputPayload,
                x.OutputDigest,
                IndexedAtUtc = x.FinishedAtUtc ?? x.UpdatedAtUtc
            })
            .ToArrayAsync(cancellationToken);

        var hits = new List<FarmKnowledgeHit>();
        var validSources = 0;
        foreach (var source in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digest = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(source.OutputPayload!))).ToLowerInvariant();
            if (!string.Equals(digest, source.OutputDigest, StringComparison.Ordinal)) continue;
            try
            {
                var index = JsonSerializer.Deserialize<FarmDistributedTextIndexResult>(
                    source.OutputPayload!, JsonOptions);
                if (index is null || index.Version != FarmDistributedTextIndex.Version ||
                    !string.Equals(index.DemandInputDigest,
                        FarmDeterministicWorkloads.ComputeInputDigest(source.InputPayload),
                        StringComparison.Ordinal)) continue;
            }
            catch (JsonException) { continue; }
            catch (ArgumentException) { continue; }

            validSources++;
            foreach (var passage in FarmKnowledgeRetrieval.Passages(source.InputPayload))
            {
                var scored = FarmKnowledgeRetrieval.Score(normalizedQuery, passage.Text);
                if (scored.Score <= 0) continue;
                hits.Add(new FarmKnowledgeHit(
                    source.Id, source.Label, source.OutputDigest!, source.IndexedAtUtc,
                    passage.Passage, passage.Text, scored.Score, scored.MatchedTerms));
            }
        }

        return new FarmKnowledgeSearchResult(
            normalizedQuery,
            validSources,
            hits.OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.IndexedAtUtc)
                .ThenBy(x => x.DemandId)
                .ThenBy(x => x.Passage)
                .Take(maximumHits)
                .ToArray());
    }
}
