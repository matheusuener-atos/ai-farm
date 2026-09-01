using atos.AIFarm.Core.Wallet;

namespace atos.AIFarm.Core.Application;

public sealed record WalletSnapshot(
    Guid WalletId,
    WalletBalance Balance,
    IReadOnlyList<LedgerTransaction> Transactions);

public sealed record ReservationResult(Guid ReservationId, long ReservedAmountMinor);

public interface IWalletService
{
    Task<WalletSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task GrantPromotionalAsync(long amountMinor, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<ReservationResult> ReservePromotionalAsync(Guid operationId, long amountMinor, string idempotencyKey, CancellationToken cancellationToken = default);
    Task CaptureAsync(Guid reservationId, long actualAmountMinor, string idempotencyKey, CancellationToken cancellationToken = default);
    Task ReleaseAsync(Guid reservationId, string idempotencyKey, string reason, CancellationToken cancellationToken = default);
    Task<int> ExpireReservationsAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default);
}
public sealed record AiRoute(string Provider, string Model, string PriceCatalogVersion);
public sealed record PriceEstimate(long MaximumCostMinor);
public sealed record ChatExecutionPlan(
    string Prompt,
    AiRoute Route,
    long MaximumCostMinor);
public sealed record AiProviderRequest(string Prompt, AiRoute Route);
public sealed record AiProviderResult(string Content, long InputUnits, long OutputUnits, long ActualCostMinor);

public enum ChatMessageRole
{
    User,
    Assistant
}

public enum ChatMessageStatus
{
    Completed
}

public sealed record ChatMessageSnapshot(
    Guid Id,
    ChatMessageRole Role,
    string Content,
    ChatMessageStatus Status,
    Guid OperationId,
    DateTimeOffset CreatedAtUtc,
    AiRoute? Route = null,
    long? EstimatedCostMinor = null,
    long? ActualCostMinor = null);

public sealed record ConversationSnapshot(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ChatMessageSnapshot> Messages);

public sealed record ConversationSummary(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int MessageCount,
    string? Preview);

public interface IPriceEstimator
{
    Task<PriceEstimate> EstimateAsync(string prompt, CancellationToken cancellationToken);
}

public interface IAiRouteSelector
{
    Task<AiRoute> SelectAsync(string prompt, CancellationToken cancellationToken);
}

public interface IAiProvider
{
    Task<AiProviderResult> ExecuteAsync(AiProviderRequest request, CancellationToken cancellationToken);
}

public sealed record ChatExecutionResult(
    Guid ConversationId,
    Guid OperationId,
    string Response,
    AiRoute Route,
    long EstimatedCostMinor,
    long ActualCostMinor,
    bool WasReplayed);

public interface IChatStore
{
    Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(
        CancellationToken cancellationToken = default);
    Task<ConversationSnapshot> CreateConversationAsync(
        CancellationToken cancellationToken = default);
    Task<ConversationSnapshot?> GetConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
    Task<bool> ArchiveConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
    Task<ConversationSnapshot> GetOrCreateCurrentConversationAsync(
        CancellationToken cancellationToken = default);
    Task<ChatExecutionResult?> FindAsync(string idempotencyKey, CancellationToken cancellationToken);
    Task SaveCompletedAsync(
        string prompt,
        string idempotencyKey,
        ChatExecutionResult result,
        CancellationToken cancellationToken);
}
