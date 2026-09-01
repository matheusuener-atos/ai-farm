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
    IPriceEstimator,
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

    public AiPriceCatalogEntry Get(AiRoute route)
    {
        if (route != _entry.Route)
        {
            throw new InvalidOperationException("A rota não pertence ao catálogo ativo.");
        }

        return _entry;
    }
}

public sealed class OpenAiProviderException(
    HttpStatusCode statusCode,
    string? requestId)
    : InvalidOperationException(
        $"A OpenAI recusou a execução ({(int)statusCode})." +
        (string.IsNullOrWhiteSpace(requestId) ? string.Empty : $" Request ID: {requestId}."))
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? RequestId { get; } = requestId;
}

public sealed class OpenAiResponsesProvider : IAiProviderClient, IDisposable
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
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.ResponsesEndpoint)
        {
            Content = JsonContent.Create(new
            {
                model = _options.Model,
                input = request.Prompt,
                max_output_tokens = catalogEntry.MaximumOutputUnits,
                store = false
            })
        };
        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var requestId = response.Headers.TryGetValues("x-request-id", out var values)
                ? values.FirstOrDefault()
                : null;
            throw new OpenAiProviderException(response.StatusCode, requestId);
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

        var content = ReadOutputText(root);
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

    public void Dispose() => _httpClient.Dispose();

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
