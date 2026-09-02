namespace atos.AIFarm.Core.Application;

public sealed record FarmBalanceSnapshot(
    long VerifiedComputeUnitsMilli,
    int CompletedJobs,
    long GeneratedEarnedAicMinor,
    long ConsumedAicMinor,
    long EarnedAvailableAicMinor,
    long PurchasedAvailableAicMinor,
    long PromotionalAvailableAicMinor,
    long TotalAvailableAicMinor,
    long ReservedAicMinor,
    DateTimeOffset? FirstCompletedJobAtUtc,
    DateTimeOffset? LastCompletedJobAtUtc)
{
    public bool HasFundedRewards => GeneratedEarnedAicMinor > 0;
}

public interface IFarmBalanceQuery
{
    Task<FarmBalanceSnapshot> GetAsync(CancellationToken cancellationToken = default);
}
