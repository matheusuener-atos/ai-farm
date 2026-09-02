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
}
