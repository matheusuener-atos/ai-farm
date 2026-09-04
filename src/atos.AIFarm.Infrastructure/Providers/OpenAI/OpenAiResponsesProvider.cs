using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure.Development;

namespace atos.AIFarm.Infrastructure.Providers.OpenAI;

public sealed record OpenAiProviderOptions(
    string ApiKey,
    string Model,
    string PriceCatalogVersion,
    long InputCostPerMillionMinor,
    long OutputCostPerMillionMinor,
    int MaximumOutputTokens,
    Uri ResponsesEndpoint)
{
    public const int ModelContextWindowTokens = 400_000;
    public static Uri DefaultEndpoint { get; } =
        new("https://api.openai.com/v1/responses");

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey) ||
            string.IsNullOrWhiteSpace(Model) ||
            string.IsNullOrWhiteSpace(PriceCatalogVersion) ||
            InputCostPerMillionMinor <= 0 ||
            OutputCostPerMillionMinor <= 0 ||
            MaximumOutputTokens <= 0 ||
            !ResponsesEndpoint.IsAbsoluteUri)
        {
            throw new InvalidOperationException("A configuração do Provider OpenAI é inválida.");
        }
    }
}

public sealed class OpenAiPriceCatalog :
    IAiRouteSelector,
    IAiModelCatalog,
    IPriceEstimator,
    IAiRequestPriceEstimator,
    IAiPriceCatalog
{
    private readonly AiPriceCatalogEntry _entry;

    public OpenAiPriceCatalog(OpenAiProviderOptions options)
    {
        options.Validate();
        _entry = new AiPriceCatalogEntry(
            new AiRoute("OpenAI", options.Model, options.PriceCatalogVersion),
            options.InputCostPerMillionMinor,
            options.OutputCostPerMillionMinor,
            options.MaximumOutputTokens);
    }

    public Task<AiRoute> SelectAsync(
        string prompt,
        CancellationToken cancellationToken) =>
        Task.FromResult(_entry.Route);

    public async Task<AiRoute> SelectAsync(
        string prompt,
        string? modelPreference,
        CancellationToken cancellationToken)
    {
        var route = await SelectAsync(prompt, cancellationToken);
        if (!string.IsNullOrWhiteSpace(modelPreference) &&
            !string.Equals(modelPreference, ConversationAiSettings.AutomaticModel, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(modelPreference, route.Model, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("O modelo escolhido não pertence ao catálogo OpenAI ativo.");
        }

        return route;
    }

    public IReadOnlyList<AiModelOption> GetOptions() =>
    [
        new(
            ConversationAiSettings.AutomaticModel,
            $"MELHOR DISPONÍVEL ({_entry.Route.Model})",
            _entry.MaximumOutputUnits),
        new(
            _entry.Route.Model,
            _entry.Route.Model,
            _entry.MaximumOutputUnits)
    ];

    public Task<PriceEstimate> EstimateAsync(
        string prompt,
        AiRoute route,
        CancellationToken cancellationToken)
    {
        var entry = Get(route);
        var conservativeInputUnits = checked(
            Encoding.UTF8.GetByteCount(prompt) + entry.InputEstimateOverheadUnits);
        return Task.FromResult(new PriceEstimate(entry.Cost(
            conservativeInputUnits,
            entry.MaximumOutputUnits)));
    }

    public Task<PriceEstimate> EstimateAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken)
    {
        var entry = Get(request.Route);
        var contextBytes = request.Context?.Sum(message =>
            Encoding.UTF8.GetByteCount(message.Content)) ?? 0;
        var conservativeInputUnits = checked(
            Encoding.UTF8.GetByteCount(request.Prompt) +
            contextBytes +
            (request.SystemInstruction is null
                ? 0
                : Encoding.UTF8.GetByteCount(request.SystemInstruction)) +
            (request.Attachments?.Sum(x => x.EstimatedInputTokens) ?? 0) +
            entry.InputEstimateOverheadUnits);
        var maximumOutputUnits = EffectiveMaximumOutputUnits(request, entry);
        return Task.FromResult(new PriceEstimate(entry.Cost(
            conservativeInputUnits,
            maximumOutputUnits)));
    }

    public AiPriceCatalogEntry Get(AiRoute route)
    {
        if (route != _entry.Route)
        {
            throw new InvalidOperationException("A rota não pertence ao catálogo ativo.");
        }

        return _entry;
    }

    private static int EffectiveMaximumOutputUnits(
        AiProviderRequest request,
        AiPriceCatalogEntry entry)
    {
        var requested = request.MaximumOutputTokens ?? entry.MaximumOutputUnits;
        if (requested is < ConversationAiSettings.MinimumOutputTokens ||
            requested > entry.MaximumOutputUnits)
        {
            throw new InvalidOperationException(
                $"O limite de resposta deve ficar entre {ConversationAiSettings.MinimumOutputTokens:N0} e {entry.MaximumOutputUnits:N0} tokens para esta rota.");
        }

        return requested;
    }
}

public sealed class OpenAiProviderException(
    HttpStatusCode statusCode,
    string? requestId)
    : InvalidOperationException(
        $"A OpenAI recusou a execução ({(int)statusCode})." +
        (string.IsNullOrWhiteSpace(requestId) ? string.Empty : $" Request ID: {requestId}.")),
        IAiFailureDetails
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? RequestId { get; } = requestId;
    public string FailureCode { get; } = FailureCodeFor(statusCode);
    public bool IsRetryable { get; } =
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.Conflict
            or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;
    public string UserMessage { get; } = UserMessageFor(statusCode);
    public string? DiagnosticId => RequestId;

    private static string FailureCodeFor(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest => "InvalidRequest",
        HttpStatusCode.Unauthorized => "AuthenticationFailed",
        HttpStatusCode.Forbidden => "PermissionDenied",
        HttpStatusCode.NotFound => "NotFound",
        HttpStatusCode.RequestTimeout => "ProviderTimeout",
        HttpStatusCode.Conflict => "ProviderConflict",
        HttpStatusCode.UnprocessableEntity => "UnprocessableRequest",
        HttpStatusCode.TooManyRequests => "RateLimited",
        _ when (int)statusCode >= 500 => "ProviderUnavailable",
        _ => "ProviderFailure"
    };

    private static string UserMessageFor(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
            "A OpenAI recusou os parâmetros desta solicitação.",
        HttpStatusCode.Unauthorized =>
            "A chave da OpenAI é inválida ou não está ativa.",
        HttpStatusCode.Forbidden =>
            "O projeto da OpenAI não tem permissão para esta operação.",
        HttpStatusCode.NotFound =>
            "O modelo ou recurso configurado não foi encontrado.",
        HttpStatusCode.TooManyRequests =>
            "A OpenAI atingiu um limite temporário. Aguarde e tente novamente.",
        HttpStatusCode.RequestTimeout =>
            "A OpenAI demorou demais para iniciar a resposta.",
        HttpStatusCode.Conflict =>
            "A OpenAI encontrou um conflito temporário. Tente novamente.",
        _ when (int)statusCode >= 500 =>
            "A OpenAI está temporariamente indisponível. Tente novamente.",
        _ => "A OpenAI não conseguiu concluir esta solicitação."
    };
}

public sealed class OpenAiStreamException(
    string apiCode,
    string? requestId)
    : InvalidOperationException(
        $"A OpenAI interrompeu o stream ({apiCode})." +
        (string.IsNullOrWhiteSpace(requestId) ? string.Empty : $" Request ID: {requestId}.")),
        IAiFailureDetails
{
    public string FailureCode { get; } = apiCode switch
    {
        "rate_limit_exceeded" => "RateLimited",
        "server_error" => "ProviderUnavailable",
        "insufficient_quota" or "credit_balance_exhausted" => "QuotaExhausted",
        _ => "ProviderFailure"
    };
    public bool IsRetryable { get; } =
        apiCode is "rate_limit_exceeded" or "server_error";
    public string UserMessage { get; } = apiCode switch
    {
        "rate_limit_exceeded" =>
            "A OpenAI atingiu um limite temporário. Aguarde e tente novamente.",
        "server_error" =>
            "A OpenAI interrompeu a resposta por uma falha temporária.",
        "insufficient_quota" or "credit_balance_exhausted" =>
            "A conta da OpenAI está sem créditos. Adicione créditos no painel de billing da OpenAI e tente novamente.",
        _ => "A OpenAI interrompeu a resposta antes da conclusão."
    };
    public string? DiagnosticId { get; } = requestId;
}

public sealed class OpenAiResponsesProvider :
    IAiStreamingProviderClient,
    IDisposable
{
    private readonly OpenAiProviderOptions _options;
    private readonly IAiPriceCatalog _catalog;
    private readonly HttpClient _httpClient;

    public OpenAiResponsesProvider(
        OpenAiProviderOptions options,
        IAiPriceCatalog catalog)
        : this(options, catalog, new SocketsHttpHandler())
    {
    }

    public OpenAiResponsesProvider(
        OpenAiProviderOptions options,
        IAiPriceCatalog catalog,
        HttpMessageHandler handler)
    {
        options.Validate();
        _options = options;
        _catalog = catalog;
        _httpClient = new HttpClient(handler, disposeHandler: true);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.ApiKey);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("atos-ai-farm/0.2");
    }

    public async Task<AiProviderResult> ExecuteAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken)
    {
        var catalogEntry = _catalog.Get(request.Route);
        ValidateContextWindow(request, catalogEntry);
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.ResponsesEndpoint)
        {
            Content = JsonContent.Create(BuildPayload(request, catalogEntry, stream: false))
        };
        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await BuildRequestExceptionAsync(response, cancellationToken);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        var status = root.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString()
            : null;
        if (!string.Equals(status, "completed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A OpenAI não concluiu a resposta.");
        }

        return ReadCompletedResult(root, catalogEntry);
    }

    public async Task<AiProviderResult> ExecuteStreamingAsync(
        AiProviderRequest request,
        AiTextDeltaHandler onDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onDelta);
        var catalogEntry = _catalog.Get(request.Route);
        ValidateContextWindow(request, catalogEntry);
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.ResponsesEndpoint)
        {
            Content = JsonContent.Create(BuildPayload(request, catalogEntry, stream: true))
        };
        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await BuildRequestExceptionAsync(response, cancellationToken);
        }
        var requestId = RequestId(response);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var content = new StringBuilder();
        AiProviderResult? result = null;

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
            var eventType = root.TryGetProperty("type", out var type)
                ? type.GetString()
                : null;
            switch (eventType)
            {
                case "response.output_text.delta":
                    if (root.TryGetProperty("delta", out var deltaElement) &&
                        deltaElement.GetString() is { Length: > 0 } delta)
                    {
                        content.Append(delta);
                        await onDelta(delta, cancellationToken);
                    }
                    break;
                case "response.completed":
                    if (!root.TryGetProperty("response", out var completed))
                    {
                        throw new InvalidOperationException(
                            "A OpenAI concluiu o stream sem o resultado final.");
                    }
                    result = ReadCompletedResult(
                        completed,
                        catalogEntry,
                        content.ToString());
                    break;
                case "response.failed":
                case "response.incomplete":
                case "error":
                    throw new OpenAiStreamException(
                        ReadStreamErrorCode(root),
                        requestId);
            }
        }

        return result ?? throw new InvalidOperationException(
            "O stream da OpenAI terminou sem confirmação de conclusão.");
    }

    private static AiProviderResult ReadCompletedResult(
        JsonElement root,
        AiPriceCatalogEntry catalogEntry,
        string? streamedContent = null)
    {
        var status = root.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString()
            : null;
        if (!string.Equals(status, "completed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A OpenAI não concluiu a resposta.");
        }

        var content = string.IsNullOrWhiteSpace(streamedContent)
            ? ReadOutputText(root)
            : streamedContent;
        if (!root.TryGetProperty("usage", out var usage) ||
            !usage.TryGetProperty("input_tokens", out var inputTokens) ||
            !usage.TryGetProperty("output_tokens", out var outputTokens))
        {
            throw new InvalidOperationException("A OpenAI não retornou medição de uso.");
        }

        var inputUnits = inputTokens.GetInt64();
        var outputUnits = outputTokens.GetInt64();
        return new AiProviderResult(
            content,
            inputUnits,
            outputUnits,
            catalogEntry.Cost(inputUnits, outputUnits));
    }

    private static async Task<Exception> BuildRequestExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var requestId = RequestId(response);
        // "Sem créditos" chega como recusa HTTP com código próprio no corpo;
        // sem esta distinção o usuário veria "limite temporário, tente de novo".
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("code", out var code) &&
                code.GetString() is ("insufficient_quota" or "credit_balance_exhausted") and { } quotaCode)
            {
                return new OpenAiStreamException(quotaCode, requestId);
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        {
        }
        return new OpenAiProviderException(response.StatusCode, requestId);
    }

    private static string? RequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-request-id", out var values)
            ? values.FirstOrDefault()
            : null;

    private static string ReadStreamErrorCode(JsonElement root)
    {
        if (root.TryGetProperty("code", out var directCode) &&
            directCode.GetString() is { Length: > 0 } direct)
        {
            return direct;
        }
        if (root.TryGetProperty("response", out var response) &&
            response.TryGetProperty("error", out var responseError) &&
            responseError.ValueKind == JsonValueKind.Object &&
            responseError.TryGetProperty("code", out var nestedCode) &&
            nestedCode.GetString() is { Length: > 0 } nested)
        {
            return nested;
        }

        return "unknown_error";
    }

    public void Dispose() => _httpClient.Dispose();

    private static object BuildInput(AiProviderRequest request)
    {
        if (request.Context is not { Count: > 0 } && request.Attachments is not { Count: > 0 })
        {
            return request.Prompt;
        }

        var input = (request.Context ?? [])
            .Select(message => new ResponseInputMessage(
                message.Role == ChatMessageRole.User ? "user" : "assistant",
                message.Content))
            .ToList();
        if (request.Attachments is not { Count: > 0 })
        {
            input.Add(new ResponseInputMessage("user", request.Prompt));
            return input;
        }

        var currentContent = new List<Dictionary<string, object>>();
        foreach (var attachment in request.Attachments)
        {
            var dataUrl = $"data:{attachment.ContentType};base64,{Convert.ToBase64String(attachment.Content)}";
            currentContent.Add(attachment.IsImage
                ? new Dictionary<string, object>
                {
                    ["type"] = "input_image",
                    ["image_url"] = dataUrl,
                    ["detail"] = "auto"
                }
                : new Dictionary<string, object>
                {
                    ["type"] = "input_file",
                    ["filename"] = attachment.FileName,
                    ["file_data"] = dataUrl
                });
        }
        currentContent.Add(new Dictionary<string, object>
        {
            ["type"] = "input_text",
            ["text"] = request.Prompt
        });
        input.Add(new ResponseInputMessage("user", currentContent));
        return input;
    }

    private sealed record ResponseInputMessage(string Role, object Content);

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

        var payload = new Dictionary<string, object>
        {
            ["model"] = _options.Model,
            ["input"] = BuildInput(request),
            ["max_output_tokens"] = maximumOutputTokens,
            ["store"] = false,
            ["truncation"] = "disabled"
        };
        if (!string.IsNullOrWhiteSpace(request.SystemInstruction))
        {
            payload["instructions"] = request.SystemInstruction;
        }
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
        }

        return payload;
    }

    private static void ValidateContextWindow(
        AiProviderRequest request,
        AiPriceCatalogEntry catalogEntry)
    {
        var inputBytes = Encoding.UTF8.GetByteCount(request.Prompt) +
            (request.Context?.Sum(message =>
                Encoding.UTF8.GetByteCount(message.Content)) ?? 0) +
            (request.SystemInstruction is null
                ? 0
                : Encoding.UTF8.GetByteCount(request.SystemInstruction));
        var messageOverhead = checked(((request.Context?.Count ?? 0) + 1) * 8);
        var maximumOutputTokens = request.MaximumOutputTokens ?? catalogEntry.MaximumOutputUnits;
        var conservativeUpperBound = checked(
            inputBytes +
            (request.Attachments?.Sum(x => x.EstimatedInputTokens) ?? 0) +
            messageOverhead + maximumOutputTokens);
        if (conservativeUpperBound > OpenAiProviderOptions.ModelContextWindowTokens)
        {
            throw new InvalidOperationException(
                "O contexto excede a janela segura do modelo OpenAI configurado.");
        }
    }

    private static string ReadOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var direct) &&
            direct.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(direct.GetString()))
        {
            return direct.GetString()!;
        }
        if (root.TryGetProperty("output", out var output))
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var contents))
                {
                    continue;
                }
                foreach (var content in contents.EnumerateArray())
                {
                    if (content.TryGetProperty("type", out var type) &&
                        type.GetString() == "output_text" &&
                        content.TryGetProperty("text", out var text) &&
                        !string.IsNullOrWhiteSpace(text.GetString()))
                    {
                        return text.GetString()!;
                    }
                }
            }
        }

        throw new InvalidOperationException("A OpenAI não retornou texto utilizável.");
    }
}
