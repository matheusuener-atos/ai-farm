using System.Net;
using System.Text;
using System.Text.Json;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure.Providers.OpenAI;

namespace atos.AIFarm.IntegrationTests;

public sealed class OpenAiResponsesProviderTests
{
    [Fact]
    public async Task ResponsesApiReturnsMeasuredUsageAndCatalogCost()
    {
        var options = Options();
        var catalog = new OpenAiPriceCatalog(options);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "status": "completed",
                  "output_text": "Resposta real simulada pelo transporte",
                  "usage": { "input_tokens": 120, "output_tokens": 30 }
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        using var provider = new OpenAiResponsesProvider(options, catalog, handler);
        var route = await catalog.SelectAsync("olá", CancellationToken.None);

        var result = await provider.ExecuteAsync(
            new AiProviderRequest("segredo do usuário", route),
            CancellationToken.None);

        Assert.Equal("Resposta real simulada pelo transporte", result.Content);
        Assert.Equal(120, result.InputUnits);
        Assert.Equal(30, result.OutputUnits);
        Assert.Equal(225, result.ActualCostMinor);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-api-key", handler.AuthorizationParameter);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(options.Model, body.RootElement.GetProperty("model").GetString());
        Assert.Equal("segredo do usuário", body.RootElement.GetProperty("input").GetString());
        Assert.Equal(512, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.DoesNotContain(options.ApiKey, handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogPinsRouteAndReservesMaximumOutput()
    {
        var options = Options();
        var catalog = new OpenAiPriceCatalog(options);
        var route = await catalog.SelectAsync("teste", CancellationToken.None);

        var estimate = await catalog.EstimateAsync(
            "teste",
            route,
            CancellationToken.None);

        Assert.Equal("OpenAI", route.Provider);
        Assert.Equal("gpt-5.4-mini-2026-03-17", route.Model);
        Assert.Equal("openai-test-v1", route.PriceCatalogVersion);
        Assert.True(estimate.MaximumCostMinor >= catalog.Get(route).Cost(5, 512));
        Assert.Throws<InvalidOperationException>(() => catalog.Get(
            route with { Model = "modelo-não-catalogado" }));
    }

    [Fact]
    public async Task ProviderErrorExposesStatusAndRequestIdWithoutBodyOrSecrets()
    {
        var options = Options();
        var catalog = new OpenAiPriceCatalog(options);
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(
                    "erro contendo segredo do usuário e test-api-key")
            };
            response.Headers.Add("x-request-id", "req_safe_123");
            return response;
        });
        using var provider = new OpenAiResponsesProvider(options, catalog, handler);
        var route = await catalog.SelectAsync("teste", CancellationToken.None);

        var error = await Assert.ThrowsAsync<OpenAiProviderException>(() =>
            provider.ExecuteAsync(
                new AiProviderRequest("segredo do usuário", route),
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal("req_safe_123", error.RequestId);
        Assert.DoesNotContain("segredo", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(options.ApiKey, error.Message, StringComparison.Ordinal);
    }

    private static OpenAiProviderOptions Options() => new(
        "test-api-key",
        "gpt-5.4-mini-2026-03-17",
        "openai-test-v1",
        750_000,
        4_500_000,
        512,
        new Uri("https://api.openai.test/v1/responses"));

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
