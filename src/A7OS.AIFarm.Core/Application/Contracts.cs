using A7OS.AIFarm.Core.Wallet;

namespace A7OS.AIFarm.Core.Application;

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
}
public sealed record AiRoute(string Provider, string Model, string PriceCatalogVersion);
public sealed record PriceEstimate(long MaximumCostMinor);
public sealed record AiProviderRequest(string Prompt, AiRoute Route);
public sealed record AiProviderResult(string Content, long InputUnits, long OutputUnits, long ActualCostMinor);

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
    Guid OperationId,
    string Response,
    AiRoute Route,
    long EstimatedCostMinor,
    long ActualCostMinor,
    bool WasReplayed);

public interface IChatResultStore
{
    Task<ChatExecutionResult?> FindAsync(string idempotencyKey, CancellationToken cancellationToken);
    Task SaveAsync(string idempotencyKey, ChatExecutionResult result, CancellationToken cancellationToken);
}
