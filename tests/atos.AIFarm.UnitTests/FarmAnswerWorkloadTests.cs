using System.Text.Json;
using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class FarmAnswerWorkloadTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string Input(string question, params (string Reference, string Label, string Excerpt)[] excerpts) =>
        JsonSerializer.Serialize(new FarmAnswerInput(
            FarmWorkloadCatalog.AnswerInputSchema,
            question,
            excerpts.Select(x => new FarmAnswerExcerpt(x.Reference, x.Label, x.Excerpt)).ToArray()),
            JsonOptions);

    [Fact]
    public void AnswerIsDeterministicExtractiveAndCitesSources()
    {
        var input = Input(
            "como o credito computacional nasce?",
            ("farm:abc:1:deadbeef", "Economia da Farm",
             "O credito computacional nasce do trabalho verificado. Cada job concluido gera CP. O clima ontem estava frio."),
            ("farm:def:2:cafebabe", "Outro texto",
             "Receitas de bolo nao tem relacao alguma."));

        var first = FarmDeterministicWorkloads.Execute("IRIS_ANSWER", "00", 1, input);
        var repeated = FarmDeterministicWorkloads.Execute("IRIS_ANSWER", "00", 1, input);

        Assert.Equal(first.Digest, repeated.Digest);
        Assert.Equal(first.OutputPayload, repeated.OutputPayload);
        var result = JsonSerializer.Deserialize<FarmAnswerResult>(first.OutputPayload!, JsonOptions)!;
        Assert.Equal(FarmWorkloadCatalog.AnswerOutputSchema, result.Version);
        Assert.Contains("credito computacional nasce", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bolo", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Sources, x => x.Reference == "farm:abc:1:deadbeef");
    }

    [Fact]
    public void AnswerIsHonestWhenNothingMatches()
    {
        var input = Input(
            "qual a capital da mongolia?",
            ("farm:abc:1:deadbeef", "Economia", "O ledger usa partidas dobradas."));

        var execution = FarmDeterministicWorkloads.Execute("IRIS_ANSWER", "00", 1, input);
        var result = JsonSerializer.Deserialize<FarmAnswerResult>(execution.OutputPayload!, JsonOptions)!;

        Assert.Contains("não tem esse conhecimento indexado", result.Answer);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public void MalformedInputIsRejectedBeforeExecution()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            FarmDeterministicWorkloads.Execute("IRIS_ANSWER", "00", 1, "não é json"));
        Assert.ThrowsAny<ArgumentException>(() =>
            FarmDeterministicWorkloads.Execute("IRIS_ANSWER", "00", 1,
                """{"version":"outra-versao","question":"x","excerpts":[]}"""));
    }
}
