using System.Net;
using System.Text;
using System.Text.Json;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure.Providers.Compatible;
using atos.AIFarm.Infrastructure.Providers.OpenAI;

namespace atos.AIFarm.IntegrationTests;

public sealed class OpenAiCompatibleChatProviderTests
{
    private static OpenAiCompatibleOptions Options() => new(
        "OpenRouter",
        "z-ai/glm-5.2:free",
        "compatible-glm-5.2-free-aic-v1",
        1_000,
        1_000,
        1_024,
        128_000,
        new Uri("https://openrouter.test/api/v1/chat/completions"),
        "test-compatible-key");

    private static OpenAiPriceCatalog Catalog(OpenAiCompatibleOptions options) => new(
        new AiPriceCatalogEntry(
            new AiRoute(options.ProviderLabel, options.Model, options.PriceCatalogVersion),
            options.InputCostPerMillionMinor,
            options.OutputCostPerMillionMinor,
            options.MaximumOutputTokens));

    [Fact]
    public async Task CompletionParsesContentUsageAndCost()
    {
        var options = Options();
        var catalog = Catalog(options);
        var handler = new StubHandler(request => Json(
            """{"choices":[{"message":{"role":"assistant","content":"Olá! Sou a Iris."}}],"usage":{"prompt_tokens":12,"completion_tokens":8}}"""));
        using var provider = new OpenAiCompatibleChatProvider(options, catalog, handler);
        var route = await catalog.SelectAsync("oi", CancellationToken.None);

        var result = await provider.ExecuteAsync(
            new AiProviderRequest("oi", route), CancellationToken.None);

        Assert.Equal("Olá! Sou a Iris.", result.Content);
        Assert.Equal(12, result.InputUnits);
        Assert.Equal(8, result.OutputUnits);
        Assert.True(result.ActualCostMinor >= 1);
        Assert.Contains("\"model\":\"z-ai/glm-5.2:free\"", handler.RequestBody);
        Assert.Contains("\"max_tokens\":1024", handler.RequestBody);
    }

    [Fact]
    public async Task StreamingCollectsDeltasAndFinalUsage()
    {
        var options = Options();
        var catalog = Catalog(options);
        var body = string.Join("\n",
            """data: {"choices":[{"delta":{"content":"Olá"}}]}""",
            """data: {"choices":[{"delta":{"content":", mundo!"}}]}""",
            """data: {"choices":[],"usage":{"prompt_tokens":9,"completion_tokens":4}}""",
            "data: [DONE]",
            string.Empty);
        var handler = new StubHandler(_ => Text(body));
        using var provider = new OpenAiCompatibleChatProvider(options, catalog, handler);
        var route = await catalog.SelectAsync("oi", CancellationToken.None);
        var streamed = new StringBuilder();

        var result = await provider.ExecuteStreamingAsync(
            new AiProviderRequest("oi", route),
            (delta, _) => { streamed.Append(delta); return ValueTask.CompletedTask; },
            CancellationToken.None);

        Assert.Equal("Olá, mundo!", result.Content);
        Assert.Equal("Olá, mundo!", streamed.ToString());
        Assert.Equal(9, result.InputUnits);
        Assert.Equal(4, result.OutputUnits);
        Assert.Contains("\"include_usage\":true", handler.RequestBody);
    }

    [Fact]
    public async Task StreamingWithoutUsageEstimatesUnitsInsteadOfFailing()
    {
        // Ollama e outros compatíveis podem omitir `usage` no streaming — a
        // resposta deve valer com estimativa, não quebrar.
        var options = Options();
        var catalog = Catalog(options);
        var body = string.Join("\n",
            """data: {"choices":[{"delta":{"content":"resposta local"}}]}""",
            "data: [DONE]",
            string.Empty);
        var handler = new StubHandler(_ => Text(body));
        using var provider = new OpenAiCompatibleChatProvider(options, catalog, handler);
        var route = await catalog.SelectAsync("uma pergunta", CancellationToken.None);

        var result = await provider.ExecuteStreamingAsync(
            new AiProviderRequest("uma pergunta", route),
            (_, _) => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal("resposta local", result.Content);
        Assert.True(result.InputUnits >= 1);
        Assert.True(result.OutputUnits >= 1);
        Assert.True(result.ActualCostMinor >= 1);
    }

    [Fact]
    public async Task ExhaustedFreeQuotaSurfacesAsQuotaFailureWithProviderLabel()
    {
        var options = Options();
        var catalog = Catalog(options);
        var handler = new StubHandler(_ =>
        {
            var response = Json(
                """{"error":{"code":"insufficient_quota","message":"free quota exceeded"}}""");
            response.StatusCode = HttpStatusCode.TooManyRequests;
            response.Headers.Add("x-request-id", "req_free_1");
            return response;
        });
        using var provider = new OpenAiCompatibleChatProvider(options, catalog, handler);
        var route = await catalog.SelectAsync("oi", CancellationToken.None);

        var error = await Assert.ThrowsAsync<CompatibleStreamException>(() =>
            provider.ExecuteAsync(new AiProviderRequest("oi", route), CancellationToken.None));

        Assert.Equal("QuotaExhausted", error.FailureCode);
        Assert.False(error.IsRetryable);
        Assert.Contains("OpenRouter", error.UserMessage);
        Assert.Equal("req_free_1", error.DiagnosticId);
    }

    [Fact]
    public async Task AttachmentsAreRejectedWithClearMessageBeforeHttpCall()
    {
        var options = Options();
        var catalog = Catalog(options);
        var handler = new StubHandler(_ => throw new InvalidOperationException(
            "O transporte não deveria ser chamado."));
        using var provider = new OpenAiCompatibleChatProvider(options, catalog, handler);
        var route = await catalog.SelectAsync("oi", CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(
                new AiProviderRequest(
                    "oi", route,
                    Attachments:
                    [
                        new AiAttachmentInput(
                            Guid.NewGuid(), Guid.NewGuid(), "a.png", "image/png",
                            3, new string('a', 64), [1, 2, 3], 10, true)
                    ]),
                CancellationToken.None));

        Assert.Contains("não aceita anexos", error.Message);
        Assert.Null(handler.RequestBody);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return responder(request);
        }
    }
}
