using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

[Collection("Farm dispatch persistence")]
public sealed class PostgresFarmNodeReliabilityTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task QuarantineRemovesFailingNodeFromCapacitySchedulerAndRaisesIncident()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();

        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "node reliability test");
        var now = DateTimeOffset.UtcNow;
        var clock = new FixedTimeProvider(now);
        var quarantinedNodeId = Guid.NewGuid();
        var trustedNodeId = Guid.NewGuid();
        var watchNodeId = Guid.NewGuid();
        try
        {
            await using var provider = BuildProvider(connectionString, identity, clock);
            await provider.InitializeAIFarmAsync();
            var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.FarmNodes.AddRange(
                    Agent(quarantinedNodeId, accountId, "UNRELIABLE", now),
                    Agent(trustedNodeId, accountId, "TRUSTED", now),
                    Agent(watchNodeId, accountId, "WATCH", now));
                db.FarmJobs.AddRange(
                    Outcome(accountId, quarantinedNodeId, FarmJobStatus.Failed, now.AddMinutes(-3)),
                    Outcome(accountId, quarantinedNodeId, FarmJobStatus.TimedOut, now.AddMinutes(-2)),
                    Outcome(accountId, quarantinedNodeId, FarmJobStatus.Rejected, now.AddMinutes(-1)),
                    Outcome(accountId, watchNodeId, FarmJobStatus.Failed, now.AddMinutes(-10)),
                    Outcome(accountId, trustedNodeId, FarmJobStatus.Completed, now.AddMinutes(-5)),
                    Outcome(accountId, trustedNodeId, FarmJobStatus.Completed, now.AddMinutes(-4)),
                    Outcome(accountId, trustedNodeId, FarmJobStatus.Completed, now.AddMinutes(-3)),
                    Outcome(accountId, trustedNodeId, FarmJobStatus.Completed, now.AddMinutes(-2)),
                    Outcome(accountId, trustedNodeId, FarmJobStatus.Completed, now.AddMinutes(-1)));
                await db.SaveChangesAsync();
            }

            var capacity = await provider.GetRequiredService<IFarmCapacityQuery>().GetAsync();
            var quarantined = capacity.Nodes.Single(x => x.Node.Id == quarantinedNodeId);
            var trusted = capacity.Nodes.Single(x => x.Node.Id == trustedNodeId);
            var watch = capacity.Nodes.Single(x => x.Node.Id == watchNodeId);
            Assert.True(quarantined.Reliability?.IsQuarantined);
            Assert.Equal(3, quarantined.Reliability?.ConsecutiveFailures);
            Assert.Equal(FarmNodeReliabilityBand.Trusted, trusted.Reliability?.Band);
            Assert.Equal(FarmNodeReliabilityBand.Watch, watch.Reliability?.Band);
            Assert.All(quarantined.Workloads, x =>
                Assert.Equal(FarmCapacityBlocker.Quarantined, x.Blocker));
            Assert.All(trusted.Workloads, x => Assert.True(x.Eligible));
            Assert.All(watch.Workloads, x => Assert.True(x.Eligible));

            var incidents = await provider.GetRequiredService<IOperationalIncidentService>()
                .SearchAsync(new IncidentQuery(Search: "NODE_RELIABILITY_QUARANTINE"));
            Assert.Contains(incidents.Incidents, x =>
                x.Code == "NODE_RELIABILITY_QUARANTINE" &&
                x.Reference == quarantinedNodeId.ToString("N")[..8]);

            var queue = provider.GetRequiredService<IFarmDemandQueueService>();
            var demand = await queue.SubmitAsync(new(
                "iris-text-index-v1",
                "Reliability routing",
                "Iris envia trabalho somente para capacidade confiável."));
            var dispatcher = provider.GetRequiredService<IFarmDemandDispatcher>();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                _ = await dispatcher.DispatchDueAsync();
                if ((await queue.GetDashboardAsync()).Demands.Single(x => x.Id == demand.Id)
                    .DispatchedShards == 1) break;
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                var dispatched = await db.FarmJobs.AsNoTracking()
                    .SingleAsync(x => x.DemandId == demand.Id);
                Assert.Equal(trustedNodeId, dispatched.NodeId);
                Assert.Equal(FarmJobStatus.Offered.ToString(), dispatched.Status);
            }
        }
        finally
        {
            await CleanupAsync(connectionString, accountId);
        }
    }

    private static FarmNodeRecord Agent(Guid id, Guid accountId, string name, DateTimeOffset now) => new()
    {
        Id = id,
        AccountId = accountId,
        Name = name,
        Status = FarmNodeStatus.Online.ToString(),
        VerificationLevel = FarmVerificationLevel.Detected.ToString(),
        ConnectionKind = FarmNodeConnectionKind.Agent.ToString(),
        AgentVersion = FarmAgentProtocol.CurrentAgentVersion,
        AgentCredentialHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        AgentCredentialIssuedAtUtc = now,
        ConfirmedAtUtc = now,
        AgentAcceptJobs = true,
        AgentMaxLogicalProcessors = 1,
        AgentMaxMemoryBytes = 256L * 1024 * 1024,
        Runtime = ".NET 10 // reliability test",
        LogicalProcessors = 2,
        MemoryBytes = 512L * 1024 * 1024,
        CreatedAtUtc = now.AddHours(-1),
        LastSeenAtUtc = now
    };

    private static FarmJobRecord Outcome(
        Guid accountId,
        Guid nodeId,
        FarmJobStatus status,
        DateTimeOffset finishedAtUtc) => new()
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            NodeId = nodeId,
            Workload = "SHA256_CHAIN",
            WorkloadVersion = "atos-sha256-chain-v1",
            VerificationPolicy = "DETERMINISTIC_REEXECUTION_V1",
            Nonce = Guid.NewGuid().ToString("N"),
            Status = status.ToString(),
            CreatedAtUtc = finishedAtUtc.AddSeconds(-1),
            StartedAtUtc = finishedAtUtc.AddSeconds(-1),
            FinishedAtUtc = finishedAtUtc,
            ComputeUnitsMilli = status == FarmJobStatus.Completed ? 1_000 : 0,
            FailureReason = status == FarmJobStatus.Completed ? null : "Synthetic reliability outcome."
        };

    private static ServiceProvider BuildProvider(
        string connectionString,
        AIFarmIdentity identity,
        TimeProvider? timeProvider = null)
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
        await using var provider = BuildProvider(
            connectionString,
            new AIFarmIdentity(accountId, accountId, "reliability cleanup"));
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var jobIds = await db.FarmJobs.Where(x => x.AccountId == accountId)
            .Select(x => x.Id).ToArrayAsync();
        db.FarmJobEvents.RemoveRange(db.FarmJobEvents.Where(x => jobIds.Contains(x.JobId)));
        db.FarmJobs.RemoveRange(db.FarmJobs.Where(x => x.AccountId == accountId));
        await db.SaveChangesAsync();
        db.FarmDemands.RemoveRange(db.FarmDemands.Where(x => x.AccountId == accountId));
        db.FarmNodes.RemoveRange(db.FarmNodes.Where(x => x.AccountId == accountId));
        db.OperationalIncidentAcknowledgements.RemoveRange(
            db.OperationalIncidentAcknowledgements.Where(x => x.AccountId == accountId));
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



    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
