using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using atos.AIFarm.Core.Application;

namespace atos.AIFarm.Agent;

/// <summary>
/// Runtime local do modelo da plataforma (ADR 0043): o job IRIS_GENERATE não
/// passa pelo worker sandbox — é uma chamada HTTP ao Ollama da própria
/// máquina (127.0.0.1:11434), com a política LOCALHOST_MODEL_V1 declarada no
/// manifesto. Sem runtime instalado, o job é devolvido e outro node tenta.
/// </summary>
internal static class AgentModelRuntime
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:11434"),
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task<AgentWorkerResult> GenerateAsync(
        RemoteJobManifest manifest,
        string inputPayload,
        CancellationToken cancellationToken)
    {
        FarmGenerateInput? input;
        try
        {
            input = JsonSerializer.Deserialize<FarmGenerateInput>(inputPayload, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("A entrada de geração não é um JSON válido.", exception);
        }
        if (input is null ||
            input.Version != FarmWorkloadCatalog.GenerateInputSchema ||
            string.IsNullOrWhiteSpace(input.Model) ||
            string.IsNullOrWhiteSpace(input.Question))
            throw new InvalidOperationException("A entrada de geração viola o esquema tipado.");

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(input.SystemInstruction))
            messages.Add(new { role = "system", content = input.SystemInstruction });
        var contextBlock = input.Context is { Count: > 0 }
            ? "Contexto da conversa:\n" + string.Join("\n", input.Context) + "\n\n"
            : string.Empty;
        messages.Add(new { role = "user", content = contextBlock + input.Question });

        var stopwatch = Stopwatch.StartNew();
        using var response = await Http.PostAsJsonUncheckedAsync(
            "/v1/chat/completions",
            new
            {
                model = input.Model,
                messages,
                stream = false,
                temperature = 0.7
            },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"O runtime local recusou a geração ({(int)response.StatusCode}): {Truncate(body, 160)}. " +
                $"Confirme 'ollama pull {input.Model}'.");
        }
        var completion = JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync(cancellationToken));
        var answer = completion.TryGetProperty("choices", out var choices) &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content)
                ? content.GetString() ?? string.Empty
                : string.Empty;
        if (string.IsNullOrWhiteSpace(answer))
            throw new InvalidOperationException("O runtime local devolveu uma geração vazia.");
        stopwatch.Stop();

        long? promptUnits = null;
        long? completionUnits = null;
        if (completion.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var prompt)) promptUnits = prompt.GetInt64();
            if (usage.TryGetProperty("completion_tokens", out var completed)) completionUnits = completed.GetInt64();
        }

        var output = JsonSerializer.Serialize(
            new FarmGenerateResult(
                FarmWorkloadCatalog.GenerateOutputSchema,
                input.Model,
                Truncate(answer, 100_000),
                promptUnits,
                completionUnits),
            JsonOptions);
        return new AgentWorkerResult(
            AgentWorkerProtocol.Version,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant(),
            Math.Max(1, stopwatch.ElapsedMilliseconds),
            0,
            Math.Max(1, stopwatch.ElapsedMilliseconds),
            "LOCAL_MODEL_RUNTIME",
            output);
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static Task<HttpResponseMessage> PostAsJsonUncheckedAsync(
        this HttpClient client,
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        return client.SendAsync(request, cancellationToken);
    }
}
