using System.Text.Json;
using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class FarmUsefulWorkloadTests
{
    [Fact]
    public void TextIndexProducesCanonicalDeterministicOutput()
    {
        const string input = "Iris organiza contexto.  IRIS organiza conhecimento!";
        var first = FarmDeterministicWorkloads.Execute(
            "IRIS_TEXT_INDEX", Guid.Empty.ToString("N"), 1, input);
        var second = FarmDeterministicWorkloads.Execute(
            "IRIS_TEXT_INDEX", Guid.NewGuid().ToString("N"), 1, input);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Digest.Length);
        using var json = JsonDocument.Parse(Assert.IsType<string>(first.OutputPayload));
        Assert.Equal("atos-text-index-v1", json.RootElement.GetProperty("version").GetString());
        Assert.Equal(6, json.RootElement.GetProperty("token_count").GetInt32());
        Assert.Equal(4, json.RootElement.GetProperty("unique_token_count").GetInt32());
        Assert.Equal("iris", json.RootElement.GetProperty("top_terms")[0].GetProperty("term").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("top_terms")[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public void UsefulRequestBindsValidatedInputToCatalogContract()
    {
        var submission = new FarmUsefulJobRequest(
            "iris-text-index-v1", "Documentação Iris", "Texto útil").Validate();

        Assert.Equal("atos-text-input-v1", submission.InputVersion);
        Assert.Equal(2_000, submission.Definition.ProcessingPointsMilli);
        Assert.Equal(FarmDeterministicWorkloads.ComputeInputDigest("Texto útil"), submission.InputDigest);
    }

    [Fact]
    public void TextInputRejectsOversizeAndControlCharacters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FarmDeterministicWorkloads.ComputeInputDigest(new string('x', 32 * 1024 + 1)));
        Assert.Throws<ArgumentException>(() =>
            FarmDeterministicWorkloads.ComputeInputDigest("inválido\0"));
    }

    [Fact]
    public void SchedulesRejectWorkloadsThatRequirePayload()
    {
        var request = new FarmScheduleRequest("Índice diário", "iris-text-index-v1", 60);
        Assert.Throws<ArgumentException>(() => request.Validate(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DemandQueueValidatesPriorityLifetimeAndUsefulPayload()
    {
        var valid = new FarmDemandRequest(
            "iris-text-index-v1", "Fila Iris", "Trabalho útil",
            FarmDemandPriority.High, 60).Validate();
        Assert.Equal(FarmDemandPriority.High, valid.Priority);
        Assert.Equal(60, valid.LifetimeMinutes);
        Assert.Equal("atos-text-input-v1", valid.Submission.InputVersion);

        Assert.Throws<ArgumentOutOfRangeException>(() => new FarmDemandRequest(
            "iris-text-index-v1", "Fila Iris", "Trabalho útil",
            FarmDemandPriority.Normal, 4).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new FarmDemandRequest(
            "iris-text-index-v1", "Fila Iris", "Trabalho útil",
            (FarmDemandPriority)99, 60).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new FarmDemandRequest(
            "iris-text-index-v1", "Fila Iris", "Trabalho útil",
            FarmDemandPriority.Normal, 60, 1, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new FarmDemandRequest(
            "iris-text-index-v1", "Fila Iris", "Trabalho útil",
            FarmDemandPriority.Normal, 60, 1, 6).Validate());
    }

    [Fact]
    public void TextWorkPartitionerCreatesBalancedDeterministicShards()
    {
        const string input = "um dois três quatro cinco seis sete";

        var first = FarmTextWorkPartitioner.Partition(input, 3);
        var second = FarmTextWorkPartitioner.Partition(input, 3);

        Assert.Equal(["um dois três", "quatro cinco", "seis sete"], first);
        Assert.Equal(first, second);
        Assert.Equal(["um", "dois"], FarmTextWorkPartitioner.Partition("um dois", 8));
        Assert.Equal(input, Assert.Single(FarmTextWorkPartitioner.Partition(input, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FarmTextWorkPartitioner.Partition(input, 9));
    }

    [Fact]
    public void DistributedTextIndexAggregatesCanonicalShardOutputs()
    {
        const string input = "Iris distribui texto entre dois nodes úteis";
        var parts = FarmTextWorkPartitioner.Partition(input, 2);
        var shards = parts.Select((part, index) =>
        {
            var execution = FarmDeterministicWorkloads.Execute(
                "IRIS_TEXT_INDEX", Guid.NewGuid().ToString("N"), 1, part);
            return (index + 1, FarmDeterministicWorkloads.ComputeInputDigest(part),
                execution.Digest, Assert.IsType<string>(execution.OutputPayload));
        }).ToArray();

        var aggregate = FarmDistributedTextIndex.Aggregate(
            FarmDeterministicWorkloads.ComputeInputDigest(input), shards);
        var repeated = FarmDistributedTextIndex.Aggregate(
            FarmDeterministicWorkloads.ComputeInputDigest(input), shards.Reverse().ToArray());

        Assert.Equal(aggregate, repeated);
        Assert.Equal(64, aggregate.Digest.Length);
        using var json = JsonDocument.Parse(aggregate.Payload);
        Assert.Equal("atos-distributed-text-index-v1", json.RootElement.GetProperty("version").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("shard_count").GetInt32());
        Assert.Equal(7, json.RootElement.GetProperty("token_count").GetInt32());
        Assert.Throws<ArgumentException>(() => FarmDistributedTextIndex.Aggregate(
            FarmDeterministicWorkloads.ComputeInputDigest(input), [shards[1]]));
    }
}
