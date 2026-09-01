namespace atos.AIFarm.Infrastructure.Persistence;

public sealed record AIFarmIdentity(Guid AccountId, Guid WalletId, string DisplayName)
{
    public static AIFarmIdentity Development { get; } = new(
        Guid.Parse("a7a0a7a0-0000-4000-8000-000000000001"),
        Guid.Parse("a7a0a7a0-0000-4000-8000-000000000002"),
        "atos Development");
}

public interface IAIFarmIdentityAccessor
{
    Task<AIFarmIdentity> GetRequiredIdentityAsync(
        CancellationToken cancellationToken = default);
}

internal sealed class FixedAIFarmIdentityAccessor(AIFarmIdentity identity)
    : IAIFarmIdentityAccessor
{
    public Task<AIFarmIdentity> GetRequiredIdentityAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(identity);
}
