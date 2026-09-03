using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace atos.AIFarm.IntegrationTests;

public sealed class PostgresFarmEconomicsScenarioTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ScenariosAreImmutableAccountScopedAndBoundToBenchmarkEvidence()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();

        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        try
        {
            await using var providerA = BuildProvider(connectionString, accountA);
            await using var providerB = BuildProvider(connectionString, accountB);
            await providerA.InitializeAIFarmAsync();
            await providerB.InitializeAIFarmAsync();

            var lab = providerA.GetRequiredService<IFarmLabService>();
            var job = await lab.RunBenchmarkAsync();
            Assert.Equal(FarmJobStatus.Completed, job.Status);
            var evidence = Assert.IsType<FarmJobEvidenceSnapshot>(job.Evidence);
            Assert.True(evidence.IsValid);

            var storeA = providerA.GetRequiredService<IFarmEconomicsScenarioStore>();
            var saved = await storeA.SaveAsync("  Base   medida  ", Scenario(), job.Id);

            Assert.Equal("Base medida", saved.Name);
            Assert.Equal(job.Id, saved.SourceJobId);
            Assert.Equal(evidence.Digest, saved.SourceEvidenceDigest);
            Assert.True(saved.IsIntegrityValid);
            Assert.True(saved.Result.MaximumFundedReward > 0);
            Assert.Single(await storeA.ListAsync());

            var proposalsA = providerA.GetRequiredService<IFarmRewardProposalStore>();
            var proposal = await proposalsA.CreateAsync(job.Id, saved.Id);
            Assert.Equal(job.Id, proposal.JobId);
            Assert.Equal(saved.Id, proposal.ScenarioId);
            Assert.Equal(1_000, proposal.ProcessingPointsMilli);
            Assert.Equal(
                decimal.Round(
                    job.ComputeUnitsMilli * saved.Result.MaximumRewardPerThousandCu / 1_000_000m,
                    6,
                    MidpointRounding.AwayFromZero),
                proposal.RecommendedRewardBrl);
            Assert.Equal(evidence.Digest, proposal.SourceEvidenceDigest);
            Assert.Equal(saved.Digest, proposal.ScenarioDigest);
            Assert.Equal(FarmRewardProposalStatus.AwaitingPolicyAndFunding, proposal.Status);
            Assert.True(proposal.IsIntegrityValid);
            Assert.Equal(proposal.Id, (await proposalsA.CreateAsync(job.Id, saved.Id)).Id);
            Assert.Single(await proposalsA.ListAsync());

            var concurrentScenario = await storeA.SaveAsync("Cenário concorrente", Scenario(), job.Id);
            var concurrentProposals = await Task.WhenAll(
                Enumerable.Range(0, 4)
                    .Select(_ => proposalsA.CreateAsync(job.Id, concurrentScenario.Id)));
            Assert.Single(concurrentProposals.Select(x => x.Id).Distinct());
            Assert.Equal(2, (await proposalsA.ListAsync()).Count);

            var analyticsA = await providerA.GetRequiredService<IFarmAnalyticsQuery>().GetAsync(7);
            Assert.Equal(1, analyticsA.JobCount);
            Assert.Equal(1, analyticsA.CompletedJobCount);
            Assert.Equal(1_000, analyticsA.VerifiedProcessingPointsMilli);
            Assert.NotNull(analyticsA.Node);
            Assert.Equal(2, analyticsA.Economics.ScenarioCount);
            Assert.Equal(2, analyticsA.Economics.RewardProposalCount);
            Assert.Equal(2_000, analyticsA.Economics.ProposedProcessingPointsMilli);

            var walletA = providerA.GetRequiredService<IWalletService>();
            var operationId = Guid.NewGuid();
            var reservation = await walletA.ReservePromotionalAsync(
                operationId, Aic.FromAic(20), "farm-balance:reserve");
            await walletA.CaptureAsync(
                reservation.ReservationId, Aic.FromAic(12), "farm-balance:capture");
            var balanceA = await providerA.GetRequiredService<IFarmBalanceQuery>().GetAsync();
            Assert.Equal(1_000, balanceA.VerifiedComputeUnitsMilli);
            Assert.Equal(1, balanceA.CompletedJobs);
            Assert.Equal(Aic.FromAic(12), balanceA.ConsumedAicMinor);
            Assert.Equal(0, balanceA.GeneratedEarnedAicMinor);
            Assert.Equal(0, balanceA.EarnedAvailableAicMinor);
            Assert.Equal(Aic.FromAic(988), balanceA.TotalAvailableAicMinor);
            Assert.False(balanceA.HasFundedRewards);

            var storeB = providerB.GetRequiredService<IFarmEconomicsScenarioStore>();
            Assert.Empty(await storeB.ListAsync());
            var farmApiB = providerB.GetRequiredService<IFarmApiJobService>();
            Assert.Null(await farmApiB.GetAsync(job.Id));
            Assert.Null(await farmApiB.DownloadEvidenceAsync(job.Id));
            var historyB = await providerB.GetRequiredService<IFarmJobHistoryService>()
                .SearchAsync(new FarmJobHistoryQuery());
            Assert.Empty(historyB.Items);
            Assert.Equal(0, historyB.Totals.Total);
            var proposalsB = providerB.GetRequiredService<IFarmRewardProposalStore>();
            Assert.Empty(await proposalsB.ListAsync());
            var analyticsB = await providerB.GetRequiredService<IFarmAnalyticsQuery>().GetAsync(7);
            Assert.Equal(0, analyticsB.JobCount);
            Assert.Null(analyticsB.Node);
            Assert.Equal(0, analyticsB.Economics.ScenarioCount);
            Assert.Equal(0, analyticsB.Economics.RewardProposalCount);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                proposalsB.CreateAsync(job.Id, saved.Id));
            var report = Assert.IsType<FarmEconomicsReportDownload>(
                await storeA.DownloadReportAsync(saved.Id));
            Assert.Equal($"atos-farm-economics-{saved.Id:N}.json", report.FileName);
            var reportJson = Encoding.UTF8.GetString(report.Content);
            Assert.Contains("atos-farm-economics-report-v1", reportJson);
            Assert.Contains(saved.Id.ToString(), reportJson);
            Assert.Contains(saved.Digest, reportJson);
            Assert.Null(await storeB.DownloadReportAsync(saved.Id));
            var balanceB = await providerB.GetRequiredService<IFarmBalanceQuery>().GetAsync();
            Assert.Equal(0, balanceB.CompletedJobs);
            Assert.Equal(0, balanceB.VerifiedComputeUnitsMilli);
            Assert.Equal(0, balanceB.ConsumedAicMinor);
            Assert.Equal(Aic.FromAic(1_000), balanceB.TotalAvailableAicMinor);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                storeB.SaveAsync("origem alheia", Scenario(), job.Id));

            var blockedScenario = await storeA.SaveAsync(
                "Evidência adulterada",
                Scenario(),
                job.Id);

            var factory = providerA.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var jobEvent = await db.FarmJobEvents.SingleAsync(
                    x => x.JobId == job.Id && x.Sequence == 1);
                jobEvent.Detail = "Evento adulterado depois da selagem.";
                var proposalRecord = await db.FarmRewardProposals.SingleAsync(x => x.Id == proposal.Id);
                proposalRecord.Digest = new string('0', 64);
                var record = await db.FarmEconomicsScenarios.SingleAsync(x => x.Id == saved.Id);
                record.Digest = new string('0', 64);
                await db.SaveChangesAsync();
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                proposalsA.CreateAsync(job.Id, blockedScenario.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                storeA.SaveAsync("Nova origem adulterada", Scenario(), job.Id));

            Assert.False((await proposalsA.ListAsync()).Single(x => x.Id == proposal.Id).IsIntegrityValid);
            var tampered = (await storeA.ListAsync()).Single(x => x.Id == saved.Id);
            Assert.False(tampered.IsIntegrityValid);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                storeA.DownloadReportAsync(saved.Id));
        }
        finally
        {
            await CleanupAsync(connectionString, accountA);
            await CleanupAsync(connectionString, accountB);
        }
    }

    private static FarmEconomicsScenario Scenario() => new(
        100m, 120m, 0.95m, 1_000m, 35m, 95m, 0.50m, 10m, 10m, 8m, 15m, 5m);

    private static ServiceProvider BuildProvider(string connectionString, Guid accountId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(
            connectionString,
            new AIFarmIdentity(accountId, accountId, "economics test"));
        return services.BuildServiceProvider();
    }

    private static async Task CleanupAsync(string connectionString, Guid accountId)
    {
        await using var provider = BuildProvider(connectionString, accountId);
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.FarmRewardProposals.RemoveRange(db.FarmRewardProposals.Where(x => x.AccountId == accountId));
        await db.SaveChangesAsync();
        db.FarmEconomicsScenarios.RemoveRange(db.FarmEconomicsScenarios.Where(x => x.AccountId == accountId));
        await db.SaveChangesAsync();
        var jobIds = await db.FarmJobs.Where(x => x.AccountId == accountId).Select(x => x.Id).ToArrayAsync();
        db.FarmJobEvents.RemoveRange(db.FarmJobEvents.Where(x => jobIds.Contains(x.JobId)));
        db.FarmJobs.RemoveRange(db.FarmJobs.Where(x => x.AccountId == accountId));
        db.FarmNodes.RemoveRange(db.FarmNodes.Where(x => x.AccountId == accountId));
        await db.SaveChangesAsync();
        var walletIds = await db.Wallets.Where(x => x.AccountId == accountId).Select(x => x.Id).ToArrayAsync();
        var transactionIds = await db.LedgerTransactions.Where(x => walletIds.Contains(x.WalletId)).Select(x => x.Id).ToArrayAsync();
        db.BalanceReservations.RemoveRange(db.BalanceReservations.Where(x => walletIds.Contains(x.WalletId)));
        db.LedgerEntries.RemoveRange(db.LedgerEntries.Where(x => transactionIds.Contains(x.TransactionId)));
        await db.SaveChangesAsync();
        db.LedgerTransactions.RemoveRange(db.LedgerTransactions.Where(x => walletIds.Contains(x.WalletId)));
        await db.SaveChangesAsync();
        db.Wallets.RemoveRange(db.Wallets.Where(x => x.AccountId == accountId));
        db.Accounts.RemoveRange(db.Accounts.Where(x => x.Id == accountId));
        await db.SaveChangesAsync();
    }

}
