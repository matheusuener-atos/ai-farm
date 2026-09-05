using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

[Collection("Farm dispatch persistence")]
public sealed class PostgresAutoRewardTests
{
    private static readonly FarmEconomicsScenario Baseline = new(
        100m, 120m, 0.95m, 1_000m, 35m, 95m, 0.50m, 10m, 10m, 8m, 15m, 5m);

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task CompletedJobAutoProposesAndBatchSettlementPaysEarned()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "auto reward test");
        try
        {
            await using var provider = BuildProvider(connectionString, identity, rewardsEnabled: true);
            await provider.InitializeAIFarmAsync();
            var lab = provider.GetRequiredService<IFarmLabService>();
            var proposals = provider.GetRequiredService<IFarmRewardProposalStore>();
            var settlements = provider.GetRequiredService<IFarmRewardSettlementService>();

            // Sem fotografia-base, completar um job NÃO propõe nada.
            var before = await lab.RunBenchmarkAsync();
            Assert.Equal(FarmJobStatus.Completed, before.Status);
            Assert.Empty(await proposals.ListAsync());

            // Com a fotografia salva (política pessoal), o próximo job
            // verificado rende proposta automaticamente.
            await provider.GetRequiredService<IFarmEconomicsScenarioStore>()
                .SaveAsync("política pessoal", Baseline);
            var job = await lab.RunBenchmarkAsync();
            Assert.Equal(FarmJobStatus.Completed, job.Status);
            var proposed = await proposals.ListAsync();
            var proposal = Assert.Single(proposed);
            Assert.Equal(job.Id, proposal.JobId);
            Assert.Equal(job.ComputeUnitsMilli, proposal.ProcessingPointsMilli);

            // Resgatar tudo liquida a proposta no bucket Earned.
            var batch = await settlements.SettleAllAsync();
            Assert.Equal(1, batch.SettledCount);
            Assert.True(batch.TotalRewardMinor >= 1);
            Assert.Equal(0, batch.RemainingCount);
            Assert.Null(batch.StoppedReason);

            // Idempotência do lote: repetir não paga de novo.
            var repeat = await settlements.SettleAllAsync();
            Assert.Equal(0, repeat.SettledCount);
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    private static ServiceProvider BuildProvider(
        string connectionString, AIFarmIdentity identity, bool rewardsEnabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(
            connectionString,
            identity,
            rewardPolicy: rewardsEnabled
                ? new FarmRewardPolicy(true, "auto-reward-test-v1", 2_000, 100_000, 10_000)
                : null);
        return services.BuildServiceProvider();
    }

    private static async Task CleanupAsync(string connectionString, Guid accountId)
    {
        await using var provider = BuildProvider(connectionString, new(accountId, accountId, "cleanup"), false);
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var proposalIds = await db.FarmRewardProposals.Where(x => x.AccountId == accountId)
            .Select(x => x.Id).ToArrayAsync();
        db.FarmRewardSettlements.RemoveRange(
            db.FarmRewardSettlements.Where(x => proposalIds.Contains(x.ProposalId)));
        await db.SaveChangesAsync();
        db.FarmRewardProposals.RemoveRange(db.FarmRewardProposals.Where(x => x.AccountId == accountId));
        db.FarmEconomicsScenarios.RemoveRange(db.FarmEconomicsScenarios.Where(x => x.AccountId == accountId));
        await db.SaveChangesAsync();
        var jobIds = await db.FarmJobs.Where(x => x.AccountId == accountId).Select(x => x.Id).ToArrayAsync();
        db.FarmJobEvents.RemoveRange(db.FarmJobEvents.Where(x => jobIds.Contains(x.JobId)));
        await db.SaveChangesAsync();
        db.FarmJobs.RemoveRange(db.FarmJobs.Where(x => x.AccountId == accountId));
        db.FarmNodes.RemoveRange(db.FarmNodes.Where(x => x.AccountId == accountId));
        await db.SaveChangesAsync();
        var walletIds = await db.Wallets.Where(x => x.AccountId == accountId)
            .Select(x => x.Id).ToArrayAsync();
        var transactionIds = await db.LedgerTransactions.Where(x => walletIds.Contains(x.WalletId))
            .Select(x => x.Id).ToArrayAsync();
        db.BalanceReservations.RemoveRange(db.BalanceReservations.Where(x => walletIds.Contains(x.WalletId)));
        db.LedgerEntries.RemoveRange(db.LedgerEntries.Where(x => transactionIds.Contains(x.TransactionId)));
        await db.SaveChangesAsync();
        db.LedgerTransactions.RemoveRange(db.LedgerTransactions.Where(x => walletIds.Contains(x.WalletId)));
        await db.SaveChangesAsync();
        db.Wallets.RemoveRange(db.Wallets.Where(x => walletIds.Contains(x.Id)));
        db.Accounts.RemoveRange(db.Accounts.Where(x => x.Id == accountId));
        await db.SaveChangesAsync();
    }
}
