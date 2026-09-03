using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

public sealed class PostgresFarmRewardSettlementTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task SettlementIsVerifiedIdempotentBudgetedAndAccountScoped()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();

        var accountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var policyVersion = $"integration-reward-{accountId:N}";
        var policy = new FarmRewardPolicy(
            true,
            policyVersion,
            Aic.FromAic(1_000),
            1_500,
            1_500);

        try
        {
            await using var provider = BuildProvider(connectionString, accountId, policy);
            await provider.InitializeAIFarmAsync();
            var lab = provider.GetRequiredService<IFarmLabService>();
            var job = await lab.RunBenchmarkAsync();
            var scenarios = provider.GetRequiredService<IFarmEconomicsScenarioStore>();
            var proposals = provider.GetRequiredService<IFarmRewardProposalStore>();
            var firstScenario = await scenarios.SaveAsync("Reward 1", Scenario(), job.Id);
            var firstProposal = await proposals.CreateAsync(job.Id, firstScenario.Id);
            var settlements = provider.GetRequiredService<IFarmRewardSettlementService>();

            var concurrent = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(_ => settlements.SettleAsync(firstProposal.Id)));
            var settlement = concurrent[0];
            Assert.Single(concurrent.Select(x => x.Id).Distinct());
            Assert.Equal(Aic.FromAic(1), settlement.RewardAmountMinor);
            Assert.Equal(firstProposal.Id, settlement.ProposalId);
            Assert.Equal(policyVersion, settlement.PolicyVersion);
            Assert.True(settlement.IsIntegrityValid);

            var pool = await settlements.GetPoolAsync();
            Assert.True(pool.Policy.IsEnabled);
            Assert.Equal(1_500, pool.BudgetMinor);
            Assert.Equal(Aic.FromAic(1), pool.SpentMinor);
            Assert.Equal(500, pool.RemainingMinor);
            Assert.Single(pool.Settlements);

            var wallet = await provider.GetRequiredService<IWalletService>().GetSnapshotAsync();
            Assert.Equal(
                Aic.FromAic(1),
                wallet.Balance.AvailableByBucket[CreditBucket.Earned]);
            var rewardTransaction = Assert.Single(
                wallet.Transactions,
                x => x.Type == LedgerTransactionType.Reward);
            Assert.Equal(settlement.LedgerTransactionId, rewardTransaction.Id);
            Assert.Equal(0, rewardTransaction.Entries.Sum(x => x.SignedAmountMinor));

            var secondScenario = await scenarios.SaveAsync("Reward 2", Scenario(), job.Id);
            var secondProposal = await proposals.CreateAsync(job.Id, secondScenario.Id);
            await Assert.ThrowsAsync<FarmRewardPoolUnavailableException>(
                () => settlements.SettleAsync(secondProposal.Id));

            await using var other = BuildProvider(connectionString, otherAccountId, policy);
            await other.InitializeAIFarmAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                other.GetRequiredService<IFarmRewardSettlementService>()
                    .SettleAsync(firstProposal.Id));

            var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var record = await db.FarmRewardSettlements.SingleAsync(x => x.Id == settlement.Id);
                record.Digest = new string('0', 64);
                await db.SaveChangesAsync();
            }
            Assert.False((await settlements.GetPoolAsync()).Settlements.Single().IsIntegrityValid);
        }
        finally
        {
            await CleanupAsync(connectionString, policyVersion, accountId, otherAccountId);
        }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task DisabledPolicyExposesZeroPoolAndRejectsSettlement()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();

        var accountId = Guid.NewGuid();
        try
        {
            await using var provider = BuildProvider(
                connectionString,
                accountId,
                FarmRewardPolicy.Disabled);
            await provider.InitializeAIFarmAsync();
            var service = provider.GetRequiredService<IFarmRewardSettlementService>();

            var pool = await service.GetPoolAsync();
            Assert.False(pool.Policy.IsEnabled);
            Assert.Equal(0, pool.BudgetMinor);
            await Assert.ThrowsAsync<FarmRewardPoolUnavailableException>(
                () => service.SettleAsync(Guid.NewGuid()));
        }
        finally
        {
            await CleanupAsync(
                connectionString,
                FarmRewardPolicy.Disabled.Version,
                accountId);
        }
    }

    private static FarmEconomicsScenario Scenario() => new(
        100m, 120m, 0.95m, 1_000m, 35m, 95m, 0.50m, 10m, 10m, 8m, 15m, 5m);

    private static ServiceProvider BuildProvider(
        string connectionString,
        Guid accountId,
        FarmRewardPolicy policy)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(
            connectionString,
            new AIFarmIdentity(accountId, accountId, "reward settlement test"),
            rewardPolicy: policy);
        return services.BuildServiceProvider();
    }

    private static async Task CleanupAsync(
        string connectionString,
        string policyVersion,
        params Guid[] accountIds)
    {
        await using var provider = BuildProvider(
            connectionString,
            accountIds[0],
            FarmRewardPolicy.Disabled);
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        db.FarmRewardSettlements.RemoveRange(
            db.FarmRewardSettlements.Where(x => accountIds.Contains(x.AccountId)));
        await db.SaveChangesAsync();
        db.FarmRewardProposals.RemoveRange(
            db.FarmRewardProposals.Where(x => accountIds.Contains(x.AccountId)));
        db.FarmEconomicsScenarios.RemoveRange(
            db.FarmEconomicsScenarios.Where(x => accountIds.Contains(x.AccountId)));
        await db.SaveChangesAsync();

        var jobIds = await db.FarmJobs
            .Where(x => accountIds.Contains(x.AccountId))
            .Select(x => x.Id)
            .ToArrayAsync();
        db.FarmJobEvents.RemoveRange(db.FarmJobEvents.Where(x => jobIds.Contains(x.JobId)));
        db.FarmJobs.RemoveRange(db.FarmJobs.Where(x => accountIds.Contains(x.AccountId)));
        db.FarmNodes.RemoveRange(db.FarmNodes.Where(x => accountIds.Contains(x.AccountId)));
        await db.SaveChangesAsync();

        var walletIds = await db.Wallets
            .Where(x => accountIds.Contains(x.AccountId))
            .Select(x => x.Id)
            .ToArrayAsync();
        var transactionIds = await db.LedgerTransactions
            .Where(x => walletIds.Contains(x.WalletId))
            .Select(x => x.Id)
            .ToArrayAsync();
        db.BalanceReservations.RemoveRange(
            db.BalanceReservations.Where(x => walletIds.Contains(x.WalletId)));
        db.LedgerEntries.RemoveRange(
            db.LedgerEntries.Where(x => transactionIds.Contains(x.TransactionId)));
        await db.SaveChangesAsync();
        db.LedgerTransactions.RemoveRange(
            db.LedgerTransactions.Where(x => walletIds.Contains(x.WalletId)));
        await db.SaveChangesAsync();
        db.Wallets.RemoveRange(db.Wallets.Where(x => accountIds.Contains(x.AccountId)));
        db.Accounts.RemoveRange(db.Accounts.Where(x => accountIds.Contains(x.Id)));
        db.FarmRewardBudgetPeriods.RemoveRange(
            db.FarmRewardBudgetPeriods.Where(x => x.PolicyVersion == policyVersion));
        await db.SaveChangesAsync();
    }

}
