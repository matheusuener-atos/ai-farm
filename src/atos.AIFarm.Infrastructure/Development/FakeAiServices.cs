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
    private readonly Dictionary<Guid, ConversationState> _conversations = [];

    public Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IReadOnlyList<ConversationSummary> summaries = _conversations.Values
                .Where(x => !x.IsArchived)
                .OrderByDescending(x => x.UpdatedAtUtc)
                .Select(x => new ConversationSummary(
                    x.Id,
                    x.Title,
                    x.CreatedAtUtc,
                    x.UpdatedAtUtc,
                    x.Messages.Count,
                    x.Messages.LastOrDefault()?.Content))
                .ToArray();
            return Task.FromResult(summaries);
        }
    }

    public Task<IReadOnlyList<ConversationSummary>> ListArchivedConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IReadOnlyList<ConversationSummary> summaries = _conversations.Values
                .Where(x => x.IsArchived)
                .OrderByDescending(x => x.UpdatedAtUtc)
                .Select(x => new ConversationSummary(
                    x.Id,
                    x.Title,
                    x.CreatedAtUtc,
                    x.UpdatedAtUtc,
                    x.Messages.Count,
                    x.Messages.LastOrDefault()?.Content))
                .ToArray();
            return Task.FromResult(summaries);
        }
    }

    public Task<ConversationSnapshot> CreateConversationAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var conversation = new ConversationState(Guid.NewGuid(), now, now);
            _conversations.Add(conversation.Id, conversation);
            return Task.FromResult(Snapshot(conversation));
        }
    }

    public Task<ConversationSnapshot?> GetConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Task.FromResult(
                _conversations.TryGetValue(conversationId, out var conversation)
                    && !conversation.IsArchived
                    ? Snapshot(conversation)
                    : null);
        }
    }

    public Task<bool> ArchiveConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_conversations.TryGetValue(conversationId, out var conversation)
                || conversation.IsArchived)
            {
                return Task.FromResult(false);
            }

            conversation.IsArchived = true;
            conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<bool> RestoreConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_conversations.TryGetValue(conversationId, out var conversation)
                || !conversation.IsArchived)
            {
                return Task.FromResult(false);
            }

            conversation.IsArchived = false;
            conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<bool> RenameConversationAsync(
        Guid conversationId,
        string title,
        CancellationToken cancellationToken = default)
    {
        var normalizedTitle = ConversationTitle.Normalize(title);
        lock (_sync)
        {
            if (!_conversations.TryGetValue(conversationId, out var conversation))
            {
                return Task.FromResult(false);
            }

            conversation.Title = normalizedTitle;
            conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<ConversationSnapshot> GetOrCreateCurrentConversationAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var conversation = _conversations.Values
                .Where(x => !x.IsArchived)
                .OrderByDescending(x => x.UpdatedAtUtc)
                .FirstOrDefault();
            if (conversation is null)
            {
                var now = DateTimeOffset.UtcNow;
                conversation = new ConversationState(Guid.NewGuid(), now, now);
                _conversations.Add(conversation.Id, conversation);
            }

            return Task.FromResult(Snapshot(conversation));
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
            if (!_conversations.TryGetValue(result.ConversationId, out var conversation)
                || conversation.IsArchived)
            {
                throw new InvalidOperationException("A conversa não existe.");
            }
            conversation.Messages.Add(new ChatMessageSnapshot(
                Guid.NewGuid(),
                ChatMessageRole.User,
                prompt,
                ChatMessageStatus.Completed,
                result.OperationId,
                now));
            conversation.Messages.Add(new ChatMessageSnapshot(
                Guid.NewGuid(),
                ChatMessageRole.Assistant,
                result.Response,
                ChatMessageStatus.Completed,
                result.OperationId,
                now,
                result.Route,
                result.EstimatedCostMinor,
                result.ActualCostMinor));
            conversation.Title ??= ConversationTitle.FromPrompt(prompt);
            conversation.UpdatedAtUtc = now;
        }

        return Task.CompletedTask;
    }

    private static ConversationSnapshot Snapshot(ConversationState conversation) => new(
        conversation.Id,
        conversation.CreatedAtUtc,
        conversation.UpdatedAtUtc,
        [.. conversation.Messages.OrderBy(x => x.CreatedAtUtc)]);

    private sealed class ConversationState(
        Guid id,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        public Guid Id { get; } = id;
        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
        public DateTimeOffset UpdatedAtUtc { get; set; } = updatedAtUtc;
        public string? Title { get; set; }
        public bool IsArchived { get; set; }
        public List<ChatMessageSnapshot> Messages { get; } = [];
    }
}
