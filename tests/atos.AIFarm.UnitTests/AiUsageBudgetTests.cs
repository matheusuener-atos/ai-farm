using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using atos.AIFarm.Infrastructure.Development;

namespace atos.AIFarm.UnitTests;

public sealed class AiUsageBudgetTests
{
    [Fact]
    public void EnabledPolicyRequiresOrderedPositiveLimits()
    {
        Assert.Throws<ArgumentException>(() => new AiUsageBudgetPolicy(true, 0, 10, 100).Validate());
        Assert.Throws<ArgumentException>(() => new AiUsageBudgetPolicy(true, 20, 10, 100).Validate());
        Assert.Throws<ArgumentException>(() => new AiUsageBudgetPolicy(true, 10, 100, 50).Validate());
        Assert.Equal(10, new AiUsageBudgetPolicy(true, 10, 20, 30).Validate().MaximumPerRequestMinor);
    }

    [Fact]
    public async Task ActiveReservationsBlockProjectedDailySpendAndReleaseRestoresCapacity()
    {
        var service = new InMemoryAiUsageBudgetService(TimeProvider.System);
        await service.UpdatePolicyAsync(new AiUsageBudgetPolicy(
            true, Aic.FromAic(20), Aic.FromAic(30), Aic.FromAic(100)));
        var first = Guid.NewGuid();
        await service.ReserveAsync(first, Aic.FromAic(20), "first");

        var error = await Assert.ThrowsAsync<AiUsageBudgetExceededException>(() =>
            service.ReserveAsync(Guid.NewGuid(), Aic.FromAic(20), "second"));
        Assert.Contains("diário", error.Message);

        await service.ReleaseAsync(first);
        await service.ReserveAsync(Guid.NewGuid(), Aic.FromAic(20), "second");
        Assert.Equal(Aic.FromAic(20), (await service.GetSnapshotAsync()).ReservedMinor);
    }

    [Fact]
    public async Task SettlementUsesActualCostAndIsIdempotent()
    {
        var service = new InMemoryAiUsageBudgetService(TimeProvider.System);
        await service.UpdatePolicyAsync(new AiUsageBudgetPolicy(
            true, Aic.FromAic(20), Aic.FromAic(100), Aic.FromAic(500)));
        var operation = Guid.NewGuid();
        await service.ReserveAsync(operation, Aic.FromAic(20), "settle");
        await service.SettleAsync(operation, Aic.FromAic(12));
        await service.SettleAsync(operation, Aic.FromAic(12));

        var snapshot = await service.GetSnapshotAsync();
        Assert.Equal(Aic.FromAic(12), snapshot.UsedTodayMinor);
        Assert.Equal(0, snapshot.ReservedMinor);
        Assert.Equal(Aic.FromAic(88), snapshot.DailyRemainingMinor);
    }
}
