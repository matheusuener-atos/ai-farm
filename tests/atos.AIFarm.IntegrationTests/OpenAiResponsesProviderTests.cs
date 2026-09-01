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
    public async Task ResponsesApiStreamsTextDeltasAndReturnsFinalUsage()
    {
        var options = Options();
        var catalog = new OpenAiPriceCatalog(options);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                data: {"type":"response.created","response":{"status":"in_progress"}}

                data: {"type":"response.output_text.delta","delta":"Olá "}

                data: {"type":"response.output_text.delta","delta":"mundo"}

                data: {"type":"response.completed","response":{"status":"completed","output_text":"Olá mundo","usage":{"input_tokens":120,"output_tokens":30}}}

                data: [DONE]

                """,
                Encoding.UTF8,
                "text/event-stream")
        });
        using var provider = new OpenAiResponsesProvider(options, catalog, handler);
        var route = await catalog.SelectAsync("olá", CancellationToken.None);
        var deltas = new List<string>();

        var result = await provider.ExecuteStreamingAsync(
            new AiProviderRequest("segredo do usuário", route),
            (delta, _) =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(["Olá ", "mundo"], deltas);
        Assert.Equal("Olá mundo", result.Content);
        Assert.Equal(120, result.InputUnits);
        Assert.Equal(30, result.OutputUnits);
        Assert.Equal(225, result.ActualCostMinor);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
    }

    [Fact]
    public async Task ResponsesApiReceivesExplicitOrderedConversationContext()
    {
        var options = Options();
        var catalog = new OpenAiPriceCatalog(options);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "status": "completed",
                  "output_text": "ok",
                  "usage": { "input_tokens": 80, "output_tokens": 5 }
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        using var provider = new OpenAiResponsesProvider(options, catalog, handler);
        var route = await catalog.SelectAsync("atual", CancellationToken.None);

        await provider.ExecuteAsync(
            new AiProviderRequest(
                "mensagem atual",
                route,
                [
                    new AiContextMessage(
                        ChatMessageRole.User,
                        "resumo anterior",
                        IsSummary: true),
                    new AiContextMessage(
                        ChatMessageRole.Assistant,
                        "resposta recente")
                ],
                42),
            CancellationToken.None);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var input = body.RootElement.GetProperty("input");
        Assert.Equal(JsonValueKind.Array, input.ValueKind);
        Assert.Equal(3, input.GetArrayLength());
        Assert.Equal("user", input[0].GetProperty("role").GetString());
        Assert.Equal("resumo anterior", input[0].GetProperty("content").GetString());
        Assert.Equal("assistant", input[1].GetProperty("role").GetString());
        Assert.Equal("user", input[2].GetProperty("role").GetString());
        Assert.Equal("mensagem atual", input[2].GetProperty("content").GetString());
        Assert.Equal("disabled", body.RootElement.GetProperty("truncation").GetString());
    }

    [Fact]
    public async Task ResponsesApiReceivesConversationExecutionProfile()
    {
        var options = Options();
        var catalog = new OpenAiPriceCatalog(options);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "status": "completed",
                  "output_text": "ok",
                  "usage": { "input_tokens": 20, "output_tokens": 5 }
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        using var provider = new OpenAiResponsesProvider(options, catalog, handler);
        var route = await catalog.SelectAsync("teste", CancellationToken.None);

        await provider.ExecuteAsync(
            new AiProviderRequest(
                "teste",
                route,
                SystemInstruction: "Responda em português.",
                Temperature: 0.3m,
                MaximumOutputTokens: 128),
            CancellationToken.None);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("Responda em português.", body.RootElement.GetProperty("instructions").GetString());
        Assert.Equal(0.3m, body.RootElement.GetProperty("temperature").GetDecimal());
        Assert.Equal(128, body.RootElement.GetProperty("max_output_tokens").GetInt32());

        var estimate = await catalog.EstimateAsync(
            new AiProviderRequest("teste", route, MaximumOutputTokens: 128),
            CancellationToken.None);
        Assert.True(estimate.MaximumCostMinor >= catalog.Get(route).Cost(5, 128));
        Assert.True(estimate.MaximumCostMinor < catalog.Get(route).Cost(5, 512));
    }

    [Fact]
    public async Task ProviderRejectsContextAboveModelWindowBeforeHttpCall()
    {
        var options = Options();
        var catalog = new OpenAiPriceCatalog(options);
        var handler = new RecordingHandler(_ => throw new InvalidOperationException(
            "O transporte não deveria ser chamado."));
        using var provider = new OpenAiResponsesProvider(options, catalog, handler);
        var route = await catalog.SelectAsync("atual", CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(
                new AiProviderRequest(
                    "mensagem atual",
                    route,
                    [new AiContextMessage(
                        ChatMessageRole.User,
                        new string('x', OpenAiProviderOptions.ModelContextWindowTokens))]),
                CancellationToken.None));

        Assert.Contains("janela segura", error.Message);
        Assert.Null(handler.RequestBody);
    }

    [Fact]
    public async Task FailedStreamExposesSafeRetryDetailsAfterPartialOutput()
    {
        var options = Options();
        var catalog = new OpenAiPriceCatalog(options);
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    data: {"type":"response.output_text.delta","delta":"parcial"}

                    data: {"type":"response.failed","response":{"status":"failed","error":{"code":"server_error","message":"conteúdo não deve vazar"}}}

                    """,
                    Encoding.UTF8,
                    "text/event-stream")
            };
            response.Headers.Add("x-request-id", "req_stream_failure");
            return response;
        });
        using var provider = new OpenAiResponsesProvider(options, catalog, handler);
        var route = await catalog.SelectAsync("teste", CancellationToken.None);
        var content = new StringBuilder();

        var error = await Assert.ThrowsAsync<OpenAiStreamException>(() =>
            provider.ExecuteStreamingAsync(
                new AiProviderRequest("segredo", route),
                (delta, _) =>
                {
                    content.Append(delta);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None));

        Assert.Equal("parcial", content.ToString());
        Assert.Equal("ProviderUnavailable", error.FailureCode);
        Assert.True(error.IsRetryable);
        Assert.Equal("req_stream_failure", error.DiagnosticId);
        Assert.DoesNotContain("conteúdo não deve vazar", error.Message);
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
        Assert.Equal("RateLimited", error.FailureCode);
        Assert.True(error.IsRetryable);
        Assert.Equal("req_safe_123", error.DiagnosticId);
        Assert.Contains("limite temporário", error.UserMessage);
        Assert.DoesNotContain("segredo", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(options.ApiKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "InvalidRequest", false)]
    [InlineData(HttpStatusCode.Unauthorized, "AuthenticationFailed", false)]
    [InlineData(HttpStatusCode.Forbidden, "PermissionDenied", false)]
    [InlineData(HttpStatusCode.RequestTimeout, "ProviderTimeout", true)]
    [InlineData(HttpStatusCode.Conflict, "ProviderConflict", true)]
    [InlineData(HttpStatusCode.TooManyRequests, "RateLimited", true)]
    [InlineData(HttpStatusCode.InternalServerError, "ProviderUnavailable", true)]
    public void ProviderErrorsExposeSafeRecoveryClassification(
        HttpStatusCode statusCode,
        string failureCode,
        bool retryable)
    {
        var error = new OpenAiProviderException(statusCode, "req_diagnostic");

        Assert.Equal(failureCode, error.FailureCode);
        Assert.Equal(retryable, error.IsRetryable);
        Assert.False(string.IsNullOrWhiteSpace(error.UserMessage));
        Assert.Equal("req_diagnostic", error.DiagnosticId);
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
