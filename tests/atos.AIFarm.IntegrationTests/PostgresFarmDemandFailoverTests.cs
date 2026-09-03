using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

[Collection("Farm dispatch persistence")]
public sealed class PostgresFarmDemandFailoverTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task FailedShardRetriesOnAnotherNodeAndPreservesAttemptHistory()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "demand failover test");
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            await using var provider = BuildProvider(connectionString, identity, clock);
            await provider.InitializeAIFarmAsync();
            var lab = provider.GetRequiredService<IFarmLabService>();
            await lab.SetNodeStatusAsync(FarmNodeStatus.Paused);
            var agents = provider.GetRequiredService<IFarmAgentService>();
            var first = await EnrollAsync(agents, "FAILOVER-A", 128L * 1024 * 1024);
            var second = await EnrollAsync(agents, "FAILOVER-B", 256L * 1024 * 1024);
            var byNode = new Dictionary<Guid, FarmAgentEnrollmentResult>
            {
                [first.NodeId] = first,
                [second.NodeId] = second
            };

            var queue = provider.GetRequiredService<IFarmDemandQueueService>();
            var dispatcher = provider.GetRequiredService<IFarmDemandDispatcher>();
            var demand = await queue.SubmitAsync(new FarmDemandRequest(
                "iris-text-index-v1", "Failover real", "Iris recupera um shard em outro node.",
                FarmDemandPriority.High, 60, TargetShards: 1, MaxAttemptsPerShard: 3));
            _ = await dispatcher.DispatchDueAsync();
            var firstAttempt = Assert.Single((await queue.GetDashboardAsync()).Demands
                .Single(x => x.Id == demand.Id).Shards).AttemptHistory.Single();
            Assert.Equal(1, firstAttempt.Attempt);

            var firstAgent = byNode[firstAttempt.NodeId];
            var failedOffer = Assert.IsType<FarmRemoteJobOffer>(
                await agents.ClaimJobAsync(firstAgent.Credential));
            Assert.True(await agents.MarkJobStartedAsync(firstAgent.Credential, failedOffer.JobId));
            var failedInput = Assert.IsType<FarmRemoteJobInput>(failedOffer.Input);
            var failedTrust = Assert.IsType<FarmAgentTrust>(await agents.GetTrustAsync(firstAgent.Credential));
            Assert.True(FarmManifestSecurity.TryVerify(
                failedOffer.ManifestPayload, failedOffer.ManifestDigest, failedOffer.ManifestSignature,
                new FarmManifestSigningOptions(failedTrust.VerificationKey), out var failedManifest));
            var failedExecution = FarmDeterministicWorkloads.Execute(
                failedManifest!.Workload, failedManifest.Nonce, failedManifest.Iterations, failedInput.Payload);
            var rejected = await agents.SubmitResultAsync(
                firstAgent.Credential, failedOffer.JobId,
                RemoteResult(new string('0', 64)) with { OutputPayload = failedExecution.OutputPayload });
            Assert.False(rejected?.Verified);

            clock.Advance(TimeSpan.FromSeconds(6));
            _ = await dispatcher.DispatchDueAsync();
            var waiting = (await queue.GetDashboardAsync()).Demands.Single(x => x.Id == demand.Id);
            Assert.Equal(FarmDemandStatus.Processing, waiting.Status);
            Assert.True(Assert.Single(waiting.Shards).RetryPending);
            Assert.Contains("Failover", waiting.Detail, StringComparison.OrdinalIgnoreCase);

            clock.Advance(TimeSpan.FromSeconds(6));
            _ = await dispatcher.DispatchDueAsync();
            var retried = (await queue.GetDashboardAsync()).Demands.Single(x => x.Id == demand.Id);
            var shard = Assert.Single(retried.Shards);
            Assert.Equal(2, shard.AttemptCount);
            Assert.Equal(2, shard.AttemptHistory.Count);
            Assert.Equal(FarmJobStatus.Failed, shard.AttemptHistory[0].Status);
            Assert.NotEqual(shard.AttemptHistory[0].NodeId, shard.AttemptHistory[1].NodeId);

            var secondAttempt = shard.AttemptHistory[1];
            var secondAgent = byNode[secondAttempt.NodeId];
            var successfulOffer = Assert.IsType<FarmRemoteJobOffer>(
                await agents.ClaimJobAsync(secondAgent.Credential));
            Assert.True(await agents.MarkJobStartedAsync(secondAgent.Credential, successfulOffer.JobId));
            var successfulInput = Assert.IsType<FarmRemoteJobInput>(successfulOffer.Input);
            var successfulTrust = Assert.IsType<FarmAgentTrust>(await agents.GetTrustAsync(secondAgent.Credential));
            Assert.True(FarmManifestSecurity.TryVerify(
                successfulOffer.ManifestPayload, successfulOffer.ManifestDigest, successfulOffer.ManifestSignature,
                new FarmManifestSigningOptions(successfulTrust.VerificationKey), out var successfulManifest));
            var execution = FarmDeterministicWorkloads.Execute(
                successfulManifest!.Workload, successfulManifest.Nonce,
                successfulManifest.Iterations, successfulInput.Payload);
            var accepted = await agents.SubmitResultAsync(
                secondAgent.Credential, successfulOffer.JobId,
                RemoteResult(execution.Digest) with { OutputPayload = execution.OutputPayload });
            Assert.True(accepted?.Verified);

            clock.Advance(TimeSpan.FromSeconds(6));
            _ = await dispatcher.DispatchDueAsync();
            var completed = (await queue.GetDashboardAsync()).Demands.Single(x => x.Id == demand.Id);
            Assert.Equal(FarmDemandStatus.Completed, completed.Status);
            Assert.Equal(1, completed.CompletedShards);
            Assert.Equal(2, Assert.Single(completed.Shards).AttemptCount);
            Assert.Equal(FarmJobStatus.Completed, completed.Shards[0].AttemptHistory[1].Status);
            Assert.NotNull(await queue.DownloadOutputAsync(demand.Id));
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task DemandFailsAfterPersistedShardAttemptBudgetIsExhausted()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "demand retry budget test");
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            await using var provider = BuildProvider(connectionString, identity, clock);
            await provider.InitializeAIFarmAsync();
            var lab = provider.GetRequiredService<IFarmLabService>();
            await lab.SetNodeStatusAsync(FarmNodeStatus.Paused);
            var agents = provider.GetRequiredService<IFarmAgentService>();
            var linked = await EnrollAsync(agents, "BUDGET-A", 128L * 1024 * 1024);
            var queue = provider.GetRequiredService<IFarmDemandQueueService>();
            var dispatcher = provider.GetRequiredService<IFarmDemandDispatcher>();
            var demand = await queue.SubmitAsync(new FarmDemandRequest(
                "iris-text-index-v1", "Orçamento esgotado", "Uma tentativa é suficiente.",
                FarmDemandPriority.Normal, 60, TargetShards: 1, MaxAttemptsPerShard: 1));
            _ = await dispatcher.DispatchDueAsync();
            var offer = Assert.IsType<FarmRemoteJobOffer>(await agents.ClaimJobAsync(linked.Credential));
            Assert.True(await agents.MarkJobStartedAsync(linked.Credential, offer.JobId));
            var input = Assert.IsType<FarmRemoteJobInput>(offer.Input);
            var trust = Assert.IsType<FarmAgentTrust>(await agents.GetTrustAsync(linked.Credential));
            Assert.True(FarmManifestSecurity.TryVerify(
                offer.ManifestPayload, offer.ManifestDigest, offer.ManifestSignature,
                new FarmManifestSigningOptions(trust.VerificationKey), out var manifest));
            var execution = FarmDeterministicWorkloads.Execute(
                manifest!.Workload, manifest.Nonce, manifest.Iterations, input.Payload);
            Assert.False((await agents.SubmitResultAsync(
                linked.Credential, offer.JobId,
                RemoteResult(new string('0', 64)) with
                { OutputPayload = execution.OutputPayload }))?.Verified);

            clock.Advance(TimeSpan.FromSeconds(6));
            _ = await dispatcher.DispatchDueAsync();
            var failed = (await queue.GetDashboardAsync()).Demands.Single(x => x.Id == demand.Id);
            Assert.Equal(FarmDemandStatus.Failed, failed.Status);
            Assert.Equal(1, failed.MaxAttemptsPerShard);
            var shard = Assert.Single(failed.Shards);
            Assert.Equal(1, shard.AttemptCount);
            Assert.False(shard.RetryPending);
            Assert.Contains("esgotaram", failed.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    private static async Task<FarmAgentEnrollmentResult> EnrollAsync(
        IFarmAgentService agents, string name, long maxMemoryBytes)
    {
        var enrollment = await agents.CreateEnrollmentAsync();
        var linked = Assert.IsType<FarmAgentEnrollmentResult>(await agents.EnrollAsync(
            enrollment.Token,
            new FarmAgentEnrollmentRequest(
                name, ".NET 10 // TEST", 4, 512L * 1024 * 1024,
                FarmAgentProtocol.CurrentAgentVersion)));
        Assert.NotNull(await agents.HeartbeatAsync(linked.Credential,
            new FarmAgentHeartbeatRequest(
                ".NET 10 // TEST", 4, 512L * 1024 * 1024,
                FarmAgentProtocol.CurrentAgentVersion, true, 1, maxMemoryBytes)));
        return linked;
    }

    private static FarmRemoteJobResult RemoteResult(string digest) =>
        new(digest, 10, 32L * 1024 * 1024, 8, FarmWorkerEnforcement.ProcessMonitorCpuAffinity);

    private static ServiceProvider BuildProvider(
        string connectionString, AIFarmIdentity identity, TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider is not null) services.AddSingleton(timeProvider);
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(connectionString, identity);
        return services.BuildServiceProvider();
    }

    private static async Task CleanupAsync(string connectionString, Guid accountId)
    {
        await using var provider = BuildProvider(connectionString, new(accountId, accountId, "cleanup"));
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.FarmAgentEnrollments.RemoveRange(db.FarmAgentEnrollments.Where(x => x.AccountId == accountId));
        await db.SaveChangesAsync();
        var jobIds = await db.FarmJobs.Where(x => x.AccountId == accountId).Select(x => x.Id).ToArrayAsync();
        db.FarmJobEvents.RemoveRange(db.FarmJobEvents.Where(x => jobIds.Contains(x.JobId)));
        db.FarmDemandShards.RemoveRange(db.FarmDemandShards.Where(x =>
            db.FarmDemands.Where(d => d.AccountId == accountId).Select(d => d.Id).Contains(x.DemandId)));
        await db.SaveChangesAsync();
        db.FarmJobs.RemoveRange(db.FarmJobs.Where(x => x.AccountId == accountId));
        db.FarmDemands.RemoveRange(db.FarmDemands.Where(x => x.AccountId == accountId));
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
        db.Wallets.RemoveRange(db.Wallets.Where(x => x.AccountId == accountId));
        db.Accounts.RemoveRange(db.Accounts.Where(x => x.Id == accountId));
        await db.SaveChangesAsync();
    }



    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
