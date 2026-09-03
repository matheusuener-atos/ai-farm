namespace atos.AIFarm.Core.Application;

public sealed record AiUsageBudgetPolicy(
    bool IsEnabled,
    long MaximumPerRequestMinor,
    long DailyLimitMinor,
    long MonthlyLimitMinor)
{
    public static AiUsageBudgetPolicy Disabled { get; } = new(false, 0, 0, 0);

    public AiUsageBudgetPolicy Validate()
    {
        if (MaximumPerRequestMinor < 0 || DailyLimitMinor < 0 || MonthlyLimitMinor < 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumPerRequestMinor), "Os limites não podem ser negativos.");
        if (IsEnabled && (MaximumPerRequestMinor <= 0 || DailyLimitMinor <= 0 || MonthlyLimitMinor <= 0))
            throw new ArgumentException("Todos os limites devem ser positivos quando o orçamento estiver ativo.");
        if (IsEnabled && MaximumPerRequestMinor > DailyLimitMinor)
            throw new ArgumentException("O limite por tarefa não pode superar o limite diário.");
        if (IsEnabled && DailyLimitMinor > MonthlyLimitMinor)
            throw new ArgumentException("O limite diário não pode superar o limite mensal.");
        return this;
    }
}

public sealed record AiUsageBudgetSnapshot(
    AiUsageBudgetPolicy Policy,
    DateTimeOffset DayStartedAtUtc,
    DateTimeOffset MonthStartedAtUtc,
    long UsedTodayMinor,
    long UsedThisMonthMinor,
    long ReservedMinor,
    DateTimeOffset UpdatedAtUtc)
{
    public long? DailyRemainingMinor => Policy.IsEnabled
        ? Math.Max(0, Policy.DailyLimitMinor - UsedTodayMinor - ReservedMinor)
        : null;
    public long? MonthlyRemainingMinor => Policy.IsEnabled
        ? Math.Max(0, Policy.MonthlyLimitMinor - UsedThisMonthMinor - ReservedMinor)
        : null;
}

public sealed record AiUsageBudgetReservation(Guid OperationId, long ReservedMinor, bool IsEnforced);

public interface IAiUsageBudgetService
{
    Task<AiUsageBudgetSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<AiUsageBudgetSnapshot> UpdatePolicyAsync(
        AiUsageBudgetPolicy policy,
        CancellationToken cancellationToken = default);
    Task<AiUsageBudgetReservation> ReserveAsync(
        Guid operationId,
        long estimatedCostMinor,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
    Task SettleAsync(Guid operationId, long actualCostMinor, CancellationToken cancellationToken = default);
    Task ReleaseAsync(Guid operationId, CancellationToken cancellationToken = default);
}

public sealed class AiUsageBudgetExceededException(string message) : InvalidOperationException(message);
