using System.Collections.Concurrent;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;

namespace atos.AIFarm.Infrastructure.Development;

public sealed class FixedPriceEstimator : IPriceEstimator
{
    public Task<PriceEstimate> EstimateAsync(string prompt, CancellationToken cancellationToken) =>
        Task.FromResult(new PriceEstimate(Aic.FromAic(20)));
}
public sealed class BestAvailableRouteSelector : IAiRouteSelector
{
    public Task<AiRoute> SelectAsync(string prompt, CancellationToken cancellationToken) =>
        Task.FromResult(new AiRoute("atos Fake Provider", "atos-fake-chat-v1", "demo-2026-08"));
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

public sealed class InMemoryChatStore : IChatStore
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, ChatExecutionResult> _results = new(StringComparer.Ordinal);
    private readonly List<ChatMessageSnapshot> _messages = [];
    private Guid? _conversationId;
    private DateTimeOffset _createdAtUtc;
    private DateTimeOffset _updatedAtUtc;

    public Task<ConversationSnapshot> GetOrCreateCurrentConversationAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_conversationId is null)
            {
                _conversationId = Guid.NewGuid();
                _createdAtUtc = _updatedAtUtc = DateTimeOffset.UtcNow;
            }

            return Task.FromResult(Snapshot());
        }
    }

    public Task<ChatExecutionResult?> FindAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        _results.TryGetValue(idempotencyKey, out var result);
        return Task.FromResult(result);
    }

    public Task SaveCompletedAsync(
        string prompt,
        string idempotencyKey,
        ChatExecutionResult result,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!_results.TryAdd(idempotencyKey, result))
            {
                return Task.CompletedTask;
            }

            var now = DateTimeOffset.UtcNow;
            if (_conversationId is null)
            {
                _conversationId = result.ConversationId;
                _createdAtUtc = now;
            }
            _messages.Add(new ChatMessageSnapshot(
                Guid.NewGuid(),
                ChatMessageRole.User,
                prompt,
                ChatMessageStatus.Completed,
                result.OperationId,
                now));
            _messages.Add(new ChatMessageSnapshot(
                Guid.NewGuid(),
                ChatMessageRole.Assistant,
                result.Response,
                ChatMessageStatus.Completed,
                result.OperationId,
                now,
                result.Route,
                result.EstimatedCostMinor,
                result.ActualCostMinor));
            _updatedAtUtc = now;
        }

        return Task.CompletedTask;
    }

    private ConversationSnapshot Snapshot() => new(
        _conversationId!.Value,
        _createdAtUtc,
        _updatedAtUtc,
        [.. _messages.OrderBy(x => x.CreatedAtUtc)]);
}
