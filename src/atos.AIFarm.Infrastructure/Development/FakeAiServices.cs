using System.Collections.Concurrent;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;

namespace atos.AIFarm.Infrastructure.Development;

public sealed class FixedPriceEstimator : IPriceEstimator
{
    public Task<PriceEstimate> EstimateAsync(
        string prompt,
        AiRoute route,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PriceEstimate(Aic.FromAic(20)));
}
public sealed class BestAvailableRouteSelector : IAiRouteSelector
{
    public Task<AiRoute> SelectAsync(string prompt, CancellationToken cancellationToken) =>
        Task.FromResult(new AiRoute("atos Fake Provider", "atos-fake-chat-v1", "demo-2026-08"));
}

public sealed class FakeAiProvider :
    IAiStreamingProviderClient,
    IAiStreamingProvider
{
    public async Task<AiProviderResult> ExecuteAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(350, cancellationToken);
        var response = $"Resposta simulada para validar o ciclo financeiro: “{request.Prompt.Trim()}”";
        return new AiProviderResult(response, request.Prompt.Length, response.Length, Aic.FromAic(12));
    }

    public async Task<AiProviderResult> ExecuteStreamingAsync(
        AiProviderRequest request,
        AiTextDeltaHandler onDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onDelta);
        var response = $"Resposta simulada para validar o ciclo financeiro: “{request.Prompt.Trim()}”";
        foreach (var chunk in response.Chunk(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(45, cancellationToken);
            await onDelta(new string(chunk), cancellationToken);
        }

        return new AiProviderResult(
            response,
            request.Prompt.Length,
            response.Length,
            Aic.FromAic(12));
    }
}

public sealed class InMemoryChatStore : IChatStore, IAiUsageQuery
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, ChatExecutionResult> _results = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FailedChatExecution> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ConversationState> _conversations = [];

    public Task<ConversationPage> SearchConversationsAsync(
        ConversationQuery query,
        CancellationToken cancellationToken = default)
    {
        var normalized = query.Normalize();
        lock (_sync)
        {
            var conversations = _conversations.Values
                .Where(x => !x.IsDeleted && x.IsArchived == normalized.Archived);
            if (normalized.Search is not null)
            {
                conversations = conversations.Where(x =>
                    (x.Title?.Contains(
                        normalized.Search,
                        StringComparison.OrdinalIgnoreCase) ?? false) ||
                    x.Messages.Any(message => message.Content.Contains(
                        normalized.Search,
                        StringComparison.OrdinalIgnoreCase)));
            }

            var ordered = conversations
                .OrderByDescending(x => x.UpdatedAtUtc)
                .ThenByDescending(x => x.Id);
            var totalCount = ordered.Count();
            var items = ordered
                .Skip((normalized.Page - 1) * normalized.PageSize)
                .Take(normalized.PageSize)
                .Select(Summary)
                .ToArray();
            return Task.FromResult(new ConversationPage(
                items,
                totalCount,
                normalized.Page,
                normalized.PageSize));
        }
    }

    public Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IReadOnlyList<ConversationSummary> summaries = _conversations.Values
                .Where(x => !x.IsArchived && !x.IsDeleted)
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
                .Where(x => x.IsArchived && !x.IsDeleted)
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
                    && !conversation.IsDeleted
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
                || conversation.IsArchived
                || conversation.IsDeleted)
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
                || !conversation.IsArchived
                || conversation.IsDeleted)
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
            if (!_conversations.TryGetValue(conversationId, out var conversation)
                || conversation.IsDeleted)
            {
                return Task.FromResult(false);
            }

            conversation.Title = normalizedTitle;
            conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_conversations.TryGetValue(conversationId, out var conversation)
                || conversation.IsDeleted)
            {
                return Task.FromResult(false);
            }

            conversation.IsDeleted = true;
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
                .Where(x => !x.IsArchived && !x.IsDeleted)
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
            var now = DateTimeOffset.UtcNow;
            var storedResult = result with
            {
                StartedAtUtc = result.StartedAtUtc ?? now,
                FinishedAtUtc = result.FinishedAtUtc ?? now
            };
            if (!_results.TryAdd(idempotencyKey, storedResult))
            {
                return Task.CompletedTask;
            }

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

    public Task SaveFailedAsync(
        string idempotencyKey,
        FailedChatExecution failure,
        CancellationToken cancellationToken)
    {
        _failures.TryAdd(idempotencyKey, failure);
        return Task.CompletedTask;
    }

    public Task<AiUsageDashboard> GetDashboardAsync(
        int recentLimit = 50,
        CancellationToken cancellationToken = default)
    {
        if (recentLimit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(recentLimit));
        }

        lock (_sync)
        {
            var results = _results.Values.ToArray();
            var failures = _failures.Values.ToArray();
            var totals = new AiUsageTotals(
                results.Length + failures.Length,
                results.Select(x => x.ConversationId)
                    .Concat(failures.Select(x => x.ConversationId))
                    .Distinct()
                    .Count(),
                results.Sum(x => x.InputUnits),
                results.Sum(x => x.OutputUnits),
                results.Sum(x => x.EstimatedCostMinor) +
                    failures.Sum(x => x.EstimatedCostMinor),
                results.Sum(x => x.ActualCostMinor));
            var completed = results.Select(x => new AiExecutionSummary(
                x.OperationId,
                x.ConversationId,
                _conversations.GetValueOrDefault(x.ConversationId)?.Title,
                x.Route.Provider,
                x.Route.Model,
                x.Route.PriceCatalogVersion,
                x.InputUnits,
                x.OutputUnits,
                x.EstimatedCostMinor,
                x.ActualCostMinor,
                "Completed",
                x.StartedAtUtc ?? DateTimeOffset.MinValue,
                x.FinishedAtUtc));
            var failed = failures.Select(x => new AiExecutionSummary(
                x.OperationId,
                x.ConversationId,
                _conversations.GetValueOrDefault(x.ConversationId)?.Title,
                x.Route.Provider,
                x.Route.Model,
                x.Route.PriceCatalogVersion,
                0,
                0,
                x.EstimatedCostMinor,
                0,
                x.FailureCode,
                x.StartedAtUtc,
                x.FinishedAtUtc));
            var executions = completed
                .Concat(failed)
                .OrderByDescending(x => x.FinishedAtUtc)
                .Take(recentLimit)
                .ToArray();
            return Task.FromResult(new AiUsageDashboard(totals, executions));
        }
    }

    private static ConversationSnapshot Snapshot(ConversationState conversation) => new(
        conversation.Id,
        conversation.CreatedAtUtc,
        conversation.UpdatedAtUtc,
        [.. conversation.Messages.OrderBy(x => x.CreatedAtUtc)],
        conversation.Title);

    private static ConversationSummary Summary(ConversationState conversation) => new(
        conversation.Id,
        conversation.Title,
        conversation.CreatedAtUtc,
        conversation.UpdatedAtUtc,
        conversation.Messages.Count,
        conversation.Messages.LastOrDefault()?.Content);

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
        public bool IsDeleted { get; set; }
        public List<ChatMessageSnapshot> Messages { get; } = [];
    }
}
