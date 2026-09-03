using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace atos.AIFarm.Core.Application;

public static class FarmTextWorkPartitioner
{
    public static IReadOnlyList<string> Partition(string input, int targetShards)
    {
        _ = FarmDeterministicWorkloads.ComputeInputDigest(input);
        if (targetShards is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(targetShards));
        if (targetShards == 1) return [input];
        var tokens = Regex.Matches(input, @"\S+").Select(x => x.Value).ToArray();
        var count = Math.Min(targetShards, tokens.Length);
        var result = new string[count];
        var offset = 0;
        for (var index = 0; index < count; index++)
        {
            var remaining = tokens.Length - offset;
            var take = (int)Math.Ceiling(remaining / (double)(count - index));
            result[index] = string.Join(' ', tokens.AsSpan(offset, take).ToArray());
            offset += take;
        }
        return result;
    }
}

public sealed record FarmDistributedTextIndexShard(
    int Sequence,
    string InputDigest,
    string OutputDigest,
    FarmTextIndexResult Index);

public sealed record FarmDistributedTextIndexResult(
    string Version,
    string DemandInputDigest,
    int ShardCount,
    int CharacterCount,
    int TokenCount,
    IReadOnlyList<FarmDistributedTextIndexShard> Shards);

public static class FarmDistributedTextIndex
{
    public const string Version = "atos-distributed-text-index-v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public static (string Payload, string Digest) Aggregate(
        string demandInputDigest,
        IReadOnlyList<(int Sequence, string InputDigest, string OutputDigest, string OutputPayload)> shards)
    {
        if (shards.Count == 0) throw new ArgumentException("Nenhum shard para consolidar.", nameof(shards));
        var ordered = shards.OrderBy(x => x.Sequence).ToArray();
        if (!ordered.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(1, ordered.Length)))
            throw new ArgumentException("A sequência de shards está incompleta.", nameof(shards));
        var items = ordered.Select(x => new FarmDistributedTextIndexShard(
            x.Sequence, x.InputDigest, x.OutputDigest,
            JsonSerializer.Deserialize<FarmTextIndexResult>(x.OutputPayload, JsonOptions)
                ?? throw new InvalidOperationException("Índice de shard inválido."))).ToArray();
        var result = new FarmDistributedTextIndexResult(
            Version, demandInputDigest, items.Length,
            items.Sum(x => x.Index.CharacterCount),
            items.Sum(x => x.Index.TokenCount), items);
        var payload = JsonSerializer.Serialize(result, JsonOptions);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return (payload, digest);
    }
}
