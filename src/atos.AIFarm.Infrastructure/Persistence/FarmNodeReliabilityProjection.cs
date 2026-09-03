using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace atos.AIFarm.Infrastructure.Persistence;

internal static class FarmNodeReliabilityProjection
{
    public static async Task<IReadOnlyDictionary<Guid, FarmNodeReliabilitySnapshot>> LoadAsync(
        AIFarmDbContext db,
        IReadOnlyCollection<Guid> nodeIds,
        DateTimeOffset now,
        FarmNodeReliabilityPolicy policy,
        CancellationToken cancellationToken)
    {
        policy.Validate();
        if (nodeIds.Count == 0)
            return new Dictionary<Guid, FarmNodeReliabilitySnapshot>();

        var nodeIdsParameter = new NpgsqlParameter<Guid[]>(
            "node_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = nodeIds.ToArray()
        };
        var sampleSizeParameter = new NpgsqlParameter<int>(
            "sample_size", NpgsqlDbType.Integer)
        {
            TypedValue = policy.SampleSize
        };
        var records = await db.Database.SqlQueryRaw<FarmNodeReliabilityRow>(
            """
            SELECT ranked."NodeId", ranked."Status", ranked."FinishedAtUtc"
            FROM (
                SELECT job."NodeId", job."Status", job."FinishedAtUtc",
                       ROW_NUMBER() OVER (
                           PARTITION BY job."NodeId"
                           ORDER BY job."FinishedAtUtc" DESC, job."Id" DESC) AS "OutcomeRank"
                FROM aifarm.farm_jobs AS job
                WHERE job."NodeId" = ANY (@node_ids)
                  AND job."FinishedAtUtc" IS NOT NULL
                  AND job."Status" IN ('Completed', 'Failed', 'TimedOut', 'Rejected')
            ) AS ranked
            WHERE ranked."OutcomeRank" <= @sample_size
            """,
            nodeIdsParameter,
            sampleSizeParameter)
            .ToArrayAsync(cancellationToken);
        var byNode = records.ToLookup(x => x.NodeId);
        var result = nodeIds.ToDictionary(
            nodeId => nodeId,
            nodeId => FarmNodeReliabilityEvaluator.Evaluate(
                byNode[nodeId].Select(x => new FarmNodeJobOutcome(
                    Enum.Parse<FarmJobStatus>(x.Status),
                    x.FinishedAtUtc)),
                now,
                policy));
        return result;
    }
}

internal sealed class FarmNodeReliabilityRow
{
    public Guid NodeId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset FinishedAtUtc { get; set; }
}
