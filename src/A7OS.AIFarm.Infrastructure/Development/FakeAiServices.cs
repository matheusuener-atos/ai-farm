using System.Collections.Concurrent;
using A7OS.AIFarm.Core.Application;
using A7OS.AIFarm.Core.Wallet;

namespace A7OS.AIFarm.Infrastructure.Development;

public sealed class FixedPriceEstimator : IPriceEstimator
{
    public Task<PriceEstimate> EstimateAsync(string prompt, CancellationToken cancellationToken) =>
        Task.FromResult(new PriceEstimate(Aic.FromAic(20)));
}
public sealed class BestAvailableRouteSelector : IAiRouteSelector
{
    public Task<AiRoute> SelectAsync(string prompt, CancellationToken cancellationToken) =>
        Task.FromResult(new AiRoute("A7OS Fake Provider", "a7os-fake-chat-v1", "demo-2026-08"));
}

public sealed class FakeAiProvider : IAiProvider
{
    public async Task<AiProviderResult> ExecuteAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(350, cancellationToken);
        var response = $"Resposta simulada para validar o ciclo financeiro: “{request.Prompt.Trim()}”";
        return new AiProviderResult(response, request.Prompt.Length, response.Length, Aic.FromAic(12));
    }
}

public sealed class InMemoryChatResultStore : IChatResultStore
{
    private readonly ConcurrentDictionary<string, ChatExecutionResult> _results = new(StringComparer.Ordinal);

    public Task<ChatExecutionResult?> FindAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        _results.TryGetValue(idempotencyKey, out var result);
        return Task.FromResult(result);
    }

    public Task SaveAsync(
        string idempotencyKey,
        ChatExecutionResult result,
        CancellationToken cancellationToken)
    {
        _results.TryAdd(idempotencyKey, result);
        return Task.CompletedTask;
    }
}
