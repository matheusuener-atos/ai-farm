using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure.Development;

namespace atos.AIFarm.Infrastructure.Providers.Compatible;

/// <summary>
/// Provider para qualquer API compatível com o chat-completions da OpenAI
/// (OpenRouter, Groq, DeepSeek, Zhipu/GLM, Ollama local etc. — ADR 0032).
/// O preço é uma tarifa interna em mAIC para manter o ciclo econômico;
/// modelos gratuitos usam tarifa simbólica.
/// </summary>
public sealed record OpenAiCompatibleOptions(
    string ProviderLabel,
    string Model,
    string PriceCatalogVersion,
    long InputCostPerMillionMinor,
    long OutputCostPerMillionMinor,
    int MaximumOutputTokens,
    int ContextWindowTokens,
    Uri ChatCompletionsEndpoint,
    string? ApiKey)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProviderLabel) ||
            string.IsNullOrWhiteSpace(Model) ||
            string.IsNullOrWhiteSpace(PriceCatalogVersion) ||
            InputCostPerMillionMinor <= 0 ||
            OutputCostPerMillionMinor <= 0 ||
            MaximumOutputTokens <= 0 ||
            ContextWindowTokens <= 0 ||
            !ChatCompletionsEndpoint.IsAbsoluteUri)
        {
            throw new InvalidOperationException(
                "A configuração do provider compatível é inválida.");
        }
    }
}

public sealed class CompatibleProviderException(
    string providerLabel,
    HttpStatusCode statusCode,
    string? requestId)
    : InvalidOperationException(
        $"O provider {providerLabel} recusou a execução ({(int)statusCode})." +
        (string.IsNullOrWhiteSpace(requestId) ? string.Empty : $" Request ID: {requestId}.")),
        IAiFailureDetails
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string FailureCode { get; } = statusCode switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "InvalidRequest",
        HttpStatusCode.Unauthorized => "AuthenticationFailed",
        HttpStatusCode.Forbidden => "PermissionDenied",
        HttpStatusCode.NotFound => "NotFound",
        HttpStatusCode.RequestTimeout => "ProviderTimeout",
        HttpStatusCode.TooManyRequests => "RateLimited",
        _ when (int)statusCode >= 500 => "ProviderUnavailable",
        _ => "ProviderFailure"
    };
    public bool IsRetryable { get; } =
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;
    public string UserMessage { get; } = statusCode switch
    {
        HttpStatusCode.Unauthorized =>
            $"A chave do provider {providerLabel} é inválida ou não está ativa.",
        HttpStatusCode.NotFound =>
            $"O modelo configurado não foi encontrado no {providerLabel}.",
        HttpStatusCode.TooManyRequests =>
            $"O {providerLabel} atingiu um limite temporário. Aguarde e tente novamente.",
        _ when (int)statusCode >= 500 =>
            $"O {providerLabel} está temporariamente indisponível. Tente novamente.",
        _ => $"O {providerLabel} não conseguiu concluir esta solicitação."
    };
    public string? DiagnosticId { get; } = requestId;
}

public sealed class CompatibleStreamException(
    string providerLabel,
    string apiCode,
    string? requestId)
    : InvalidOperationException(
        $"O provider {providerLabel} interrompeu a resposta ({apiCode})." +
        (string.IsNullOrWhiteSpace(requestId) ? string.Empty : $" Request ID: {requestId}.")),
        IAiFailureDetails
{
    public string FailureCode { get; } = apiCode switch
    {
        "rate_limit_exceeded" => "RateLimited",
        "insufficient_quota" or "credit_balance_exhausted" => "QuotaExhausted",
        "server_error" => "ProviderUnavailable",
        _ => "ProviderFailure"
    };
    public bool IsRetryable { get; } =
        apiCode is "rate_limit_exceeded" or "server_error";
    public string UserMessage { get; } = apiCode switch
    {
        "rate_limit_exceeded" =>
            $"O {providerLabel} atingiu um limite temporário. Aguarde e tente novamente.",
        "insufficient_quota" or "credit_balance_exhausted" =>
            $"A conta do {providerLabel} está sem créditos ou sem cota gratuita disponível.",
        _ => $"O {providerLabel} interrompeu a resposta antes da conclusão."
    };
    public string? DiagnosticId { get; } = requestId;
}

public sealed class OpenAiCompatibleChatProvider :
    IAiStreamingProviderClient,
    IDisposable
{
    private readonly OpenAiCompatibleOptions _options;
    private readonly IAiPriceCatalog _catalog;
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleChatProvider(
        OpenAiCompatibleOptions options,
        IAiPriceCatalog catalog)
        : this(options, catalog, new SocketsHttpHandler())
    {
    }

    public OpenAiCompatibleChatProvider(
        OpenAiCompatibleOptions options,
        IAiPriceCatalog catalog,
        HttpMessageHandler handler)
    {
        options.Validate();
        _options = options;
        _catalog = catalog;
        _httpClient = new HttpClient(handler, disposeHandler: true);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("atos-ai-farm/0.2");
    }

    public async Task<AiProviderResult> ExecuteAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken)
    {
        var catalogEntry = _catalog.Get(request.Route);
        ValidateRequest(request, catalogEntry);
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.ChatCompletionsEndpoint)
        {
            Content = JsonContent.Create(BuildPayload(request, catalogEntry, stream: false))
        };
        using var response = await _httpClient.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await BuildRequestExceptionAsync(response, cancellationToken);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var content = ReadMessageContent(root);
        var (inputUnits, outputUnits) = ReadUsage(root);
        return new AiProviderResult(
            content, inputUnits, outputUnits, catalogEntry.Cost(inputUnits, outputUnits));
    }

    public async Task<AiProviderResult> ExecuteStreamingAsync(
        AiProviderRequest request,
        AiTextDeltaHandler onDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onDelta);
        var catalogEntry = _catalog.Get(request.Route);
        ValidateRequest(request, catalogEntry);
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.ChatCompletionsEndpoint)
        {
            Content = JsonContent.Create(BuildPayload(request, catalogEntry, stream: true))
        };
        using var response = await _httpClient.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await BuildRequestExceptionAsync(response, cancellationToken);
        }
        var requestId = RequestId(response);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var content = new StringBuilder();
        long? inputUnits = null;
        long? outputUnits = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }
            var data = line["data:".Length..].TrimStart();
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var streamError))
            {
                throw new CompatibleStreamException(
                    _options.ProviderLabel, ReadErrorCode(streamError), requestId);
            }
            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("content", out var deltaContent) &&
                        deltaContent.GetString() is { Length: > 0 } piece)
                    {
                        content.Append(piece);
                        await onDelta(piece, cancellationToken);
                    }
                }
            }
            if (root.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty("prompt_tokens", out var prompt) &&
                usage.TryGetProperty("completion_tokens", out var completion))
            {
                inputUnits = prompt.GetInt64();
                outputUnits = completion.GetInt64();
            }
        }

        if (content.Length == 0 || inputUnits is null || outputUnits is null)
        {
            throw new CompatibleStreamException(
                _options.ProviderLabel, "incomplete_stream", requestId);
        }
        return new AiProviderResult(
            content.ToString(),
            inputUnits.Value,
            outputUnits.Value,
            catalogEntry.Cost(inputUnits.Value, outputUnits.Value));
    }

    public void Dispose() => _httpClient.Dispose();

    private void ValidateRequest(AiProviderRequest request, AiPriceCatalogEntry catalogEntry)
    {
        if (request.Attachments is { Count: > 0 })
        {
            throw new InvalidOperationException(
                $"O provider {_options.ProviderLabel} configurado não aceita anexos.");
        }
        var inputBytes = Encoding.UTF8.GetByteCount(request.Prompt) +
            (request.Context?.Sum(message => Encoding.UTF8.GetByteCount(message.Content)) ?? 0) +
            (request.SystemInstruction is null
                ? 0
                : Encoding.UTF8.GetByteCount(request.SystemInstruction));
        var maximumOutputTokens = request.MaximumOutputTokens ?? catalogEntry.MaximumOutputUnits;
        var conservativeUpperBound = checked(
            inputBytes + ((request.Context?.Count ?? 0) + 1) * 8 + maximumOutputTokens);
        if (conservativeUpperBound > _options.ContextWindowTokens)
        {
            throw new InvalidOperationException(
                $"O contexto excede a janela segura do modelo configurado no {_options.ProviderLabel}.");
        }
    }

    private Dictionary<string, object> BuildPayload(
        AiProviderRequest request,
        AiPriceCatalogEntry catalogEntry,
        bool stream)
    {
        var maximumOutputTokens = request.MaximumOutputTokens ?? catalogEntry.MaximumOutputUnits;
        if (maximumOutputTokens is < ConversationAiSettings.MinimumOutputTokens ||
            maximumOutputTokens > catalogEntry.MaximumOutputUnits)
        {
            throw new InvalidOperationException(
                $"O limite de resposta deve ficar entre {ConversationAiSettings.MinimumOutputTokens:N0} e {catalogEntry.MaximumOutputUnits:N0} tokens para esta rota.");
        }

        var messages = new List<Dictionary<string, string>>();
        if (!string.IsNullOrWhiteSpace(request.SystemInstruction))
        {
            messages.Add(new() { ["role"] = "system", ["content"] = request.SystemInstruction });
        }
        foreach (var contextMessage in request.Context ?? [])
        {
            messages.Add(new()
            {
                ["role"] = contextMessage.Role == ChatMessageRole.User ? "user" : "assistant",
                ["content"] = contextMessage.Content
            });
        }
        messages.Add(new() { ["role"] = "user", ["content"] = request.Prompt });

        var payload = new Dictionary<string, object>
        {
            ["model"] = _options.Model,
            ["messages"] = messages,
            ["max_tokens"] = maximumOutputTokens
        };
        if (request.Temperature is decimal temperature)
        {
            if (temperature is < 0 or > 2)
            {
                throw new InvalidOperationException("A criatividade deve ficar entre 0 e 2.");
            }
            payload["temperature"] = temperature;
        }
        if (stream)
        {
            payload["stream"] = true;
            payload["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };
        }
        return payload;
    }

    private string ReadMessageContent(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content) &&
                    content.GetString() is { Length: > 0 } text)
                {
                    return text;
                }
            }
        }
        throw new InvalidOperationException(
            $"O provider {_options.ProviderLabel} não retornou texto utilizável.");
    }

    private (long InputUnits, long OutputUnits) ReadUsage(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage) &&
            usage.TryGetProperty("prompt_tokens", out var prompt) &&
            usage.TryGetProperty("completion_tokens", out var completion))
        {
            return (prompt.GetInt64(), completion.GetInt64());
        }
        throw new InvalidOperationException(
            $"O provider {_options.ProviderLabel} não retornou medição de uso.");
    }

    private async Task<Exception> BuildRequestExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var requestId = RequestId(response);
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                ReadErrorCode(error) is ("insufficient_quota" or "credit_balance_exhausted") and { } quota)
            {
                return new CompatibleStreamException(_options.ProviderLabel, quota, requestId);
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        {
        }
        return new CompatibleProviderException(_options.ProviderLabel, response.StatusCode, requestId);
    }

    private static string ReadErrorCode(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.String &&
                code.GetString() is { Length: > 0 } direct)
            {
                return direct;
            }
            if (error.TryGetProperty("type", out var type) &&
                type.GetString() is { Length: > 0 } typed)
            {
                return typed;
            }
        }
        return "unknown_error";
    }

    private static string? RequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-request-id", out var values)
            ? values.FirstOrDefault()
            : null;
}
