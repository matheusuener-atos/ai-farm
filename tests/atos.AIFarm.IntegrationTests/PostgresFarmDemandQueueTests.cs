using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace atos.AIFarm.IntegrationTests;

[Collection("Farm dispatch persistence")]
public sealed class PostgresFarmDemandQueueTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ConcurrentDispatchCreatesExactlyOneVerifiedJob()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "demand queue test");
        try
        {
            await using var providerA = BuildProvider(connectionString, identity);
            await providerA.InitializeAIFarmAsync();
            await using var providerB = BuildProvider(connectionString, identity);
            var queue = providerA.GetRequiredService<IFarmDemandQueueService>();
            var demand = await queue.SubmitAsync(new(
                "iris-text-index-v1", "Documento prioritário", "Iris processa a fila persistente.",
                FarmDemandPriority.High, 60));
            Assert.Equal(FarmDemandStatus.Queued, demand.Status);

            _ = await Task.WhenAll(
                providerA.GetRequiredService<IFarmDemandDispatcher>().DispatchDueAsync(),
                providerB.GetRequiredService<IFarmDemandDispatcher>().DispatchDueAsync());
            var dispatcher = providerA.GetRequiredService<IFarmDemandDispatcher>();
            _ = await DispatchUntilAsync(queue, dispatcher, demand.Id,
                state => state.Status == FarmDemandStatus.Submitted);

            var dashboard = await queue.GetDashboardAsync();
            var submitted = Assert.Single(dashboard.Demands);
            Assert.Equal(FarmDemandStatus.Submitted, submitted.Status);
            Assert.Equal(FarmJobStatus.Completed, submitted.JobStatus);
            Assert.NotNull(submitted.JobId);
            var factory = providerA.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var job = Assert.Single(await db.FarmJobs.AsNoTracking()
                .Where(x => x.DemandId == demand.Id).ToArrayAsync());
            Assert.Equal(submitted.JobId, job.Id);
            Assert.NotNull(await providerA.GetRequiredService<IFarmUsefulJobService>()
                .DownloadOutputAsync(job.Id));
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task QueuedDemandCanBeCancelledAndExpired()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "demand lifecycle test");
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            await using var provider = BuildProvider(connectionString, identity, clock);
            await provider.InitializeAIFarmAsync();
            var queue = provider.GetRequiredService<IFarmDemandQueueService>();
            var cancelled = await queue.SubmitAsync(new(
                "iris-text-index-v1", "Cancelar fila", "Entrada cancelável", LifetimeMinutes: 5));
            Assert.Equal(FarmDemandStatus.Cancelled, (await queue.CancelAsync(cancelled.Id)).Status);

            var expiring = await queue.SubmitAsync(new(
                "iris-text-index-v1", "Expirar fila", "Entrada expirada", LifetimeMinutes: 5));
            clock.Advance(TimeSpan.FromMinutes(6));
            var expired = (await queue.GetDashboardAsync()).Demands.Single(x => x.Id == expiring.Id);
            Assert.Equal(FarmDemandStatus.Expired, expired.Status);
            Assert.Null(expired.JobId);
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task CapacityFailureRequeuesAndLaterDispatchesToAgent()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "demand retry test");
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            await using var provider = BuildProvider(connectionString, identity, clock);
            await provider.InitializeAIFarmAsync();
            var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
            var agentId = Guid.NewGuid();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.FarmNodes.Add(new FarmNodeRecord
                {
                    Id = agentId,
                    AccountId = accountId,
                    Name = "QUEUE-AGENT",
                    Status = FarmNodeStatus.Paused.ToString(),
                    VerificationLevel = FarmVerificationLevel.Detected.ToString(),
                    ConnectionKind = FarmNodeConnectionKind.Agent.ToString(),
                    AgentVersion = FarmAgentProtocol.CurrentAgentVersion,
                    AgentCredentialHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                    AgentCredentialIssuedAtUtc = clock.GetUtcNow(),
                    ConfirmedAtUtc = clock.GetUtcNow(),
                    AgentAcceptJobs = true,
                    AgentMaxLogicalProcessors = 1,
                    AgentMaxMemoryBytes = 256L * 1024 * 1024,
                    Runtime = ".NET 10 // test",
                    LogicalProcessors = 1,
                    MemoryBytes = 512L * 1024 * 1024,
                    CreatedAtUtc = clock.GetUtcNow(),
                    LastSeenAtUtc = clock.GetUtcNow()
                });
                await db.SaveChangesAsync();
            }
            var queue = provider.GetRequiredService<IFarmDemandQueueService>();
            var dispatcher = provider.GetRequiredService<IFarmDemandDispatcher>();
            var demand = await queue.SubmitAsync(new(
                "iris-text-index-v1", "Retomar capacidade", "Fila aguarda o Agent.",
                FarmDemandPriority.Normal, 60));
            var deferred = await DispatchUntilAsync(
                queue, dispatcher, demand.Id, state => state.Attempts >= 1);
            Assert.Equal(FarmDemandStatus.Queued, deferred.Status);
            Assert.Equal(1, deferred.Attempts);
            Assert.Null(deferred.JobId);

            await using (var db = await factory.CreateDbContextAsync())
            {
                var agent = await db.FarmNodes.SingleAsync(x => x.Id == agentId);
                agent.Status = FarmNodeStatus.Online.ToString();
                agent.LastSeenAtUtc = clock.GetUtcNow().AddSeconds(6);
                await db.SaveChangesAsync();
            }
            clock.Advance(TimeSpan.FromSeconds(6));
            var submitted = await DispatchUntilAsync(
                queue, dispatcher, demand.Id, state => state.JobId is not null);
            Assert.Equal(FarmDemandStatus.Submitted, submitted.Status);
            Assert.Equal(FarmJobStatus.Offered, submitted.JobStatus);
            Assert.Equal(2, submitted.Attempts);
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ResourceAwareSchedulerUsesBestFitNodeAndUpdatesCapacityMatrix()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "capacity matrix test");
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            await using var provider = BuildProvider(connectionString, identity, clock);
            await provider.InitializeAIFarmAsync();
            var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
            var bestFitId = Guid.NewGuid();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.FarmNodes.AddRange(
                    Agent(bestFitId, accountId, "BEST-FIT", 128L * 1024 * 1024, clock.GetUtcNow()),
                    Agent(Guid.NewGuid(), accountId, "LARGE-NODE", 256L * 1024 * 1024, clock.GetUtcNow()));
                await db.SaveChangesAsync();
            }
            var capacity = provider.GetRequiredService<IFarmCapacityQuery>();
            var before = await capacity.GetAsync();
            Assert.Equal(2, before.Workloads.Single(x => x.WorkloadId == "iris-text-index-v1").EligibleNodeCount);

            var queue = provider.GetRequiredService<IFarmDemandQueueService>();
            var demand = await queue.SubmitAsync(new(
                "iris-text-index-v1", "Selecionar best fit", "Preservar o Node maior."));
            Assert.Equal(128L * 1024 * 1024, demand.RequiredMemoryBytes);
            var dispatcher = provider.GetRequiredService<IFarmDemandDispatcher>();
            _ = await DispatchUntilAsync(queue, dispatcher, demand.Id,
                state => state.DispatchedShards == 1);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var job = await db.FarmJobs.AsNoTracking().SingleAsync(x => x.DemandId == demand.Id);
                Assert.Equal(bestFitId, job.NodeId);
            }
            var after = await capacity.GetAsync();
            Assert.Equal(1, after.Workloads.Single(x => x.WorkloadId == "iris-text-index-v1").EligibleNodeCount);
            Assert.True(after.Nodes.Single(x => x.Node.Id == bestFitId).Busy);
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task DistributedDemandFansOutToTwoAgentsAndAggregatesVerifiedOutput()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "distributed demand test");
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            await using var provider = BuildProvider(connectionString, identity, clock);
            await provider.InitializeAIFarmAsync();
            var lab = provider.GetRequiredService<IFarmLabService>();
            await lab.SetNodeStatusAsync(FarmNodeStatus.Paused);
            var agents = provider.GetRequiredService<IFarmAgentService>();

            async Task<FarmAgentEnrollmentResult> EnrollAsync(string name)
            {
                var enrollment = await agents.CreateEnrollmentAsync();
                var linked = Assert.IsType<FarmAgentEnrollmentResult>(await agents.EnrollAsync(
                    enrollment.Token,
                    new FarmAgentEnrollmentRequest(
                        name, ".NET 10 // TEST", 4, 512L * 1024 * 1024,
                        FarmAgentProtocol.CurrentAgentVersion, "teste@atos.local")));
                await lab.ConfirmNodeAsync(linked.NodeId, linked.ConfirmationCode!);
                Assert.NotNull(await agents.HeartbeatAsync(linked.Credential,
                    new FarmAgentHeartbeatRequest(
                        ".NET 10 // TEST", 4, 512L * 1024 * 1024,
                        FarmAgentProtocol.CurrentAgentVersion, AcceptJobs: true,
                        MaxLogicalProcessors: 1, MaxMemoryBytes: 128L * 1024 * 1024)));
                return linked;
            }

            var first = await EnrollAsync("DISTRIBUTED-A");
            var second = await EnrollAsync("DISTRIBUTED-B");
            var queue = provider.GetRequiredService<IFarmDemandQueueService>();
            var dispatcher = provider.GetRequiredService<IFarmDemandDispatcher>();
            const string input = "Iris distribui esta demanda útil entre dois nodes independentes";
            var demand = await queue.SubmitAsync(new(
                "iris-text-index-v1", "Índice distribuído", input,
                FarmDemandPriority.High, 60, TargetShards: 2));

            var dispatched = await DispatchUntilAsync(
                queue, dispatcher, demand.Id, state => state.DispatchedShards == 2);
            Assert.Equal(FarmDemandStatus.Submitted, dispatched.Status);
            Assert.Equal(2, dispatched.DispatchedShards);
            Assert.Equal(2, dispatched.Shards.Select(x => x.JobId).Distinct().Count());

            foreach (var linked in new[] { first, second })
            {
                var offer = Assert.IsType<FarmRemoteJobOffer>(await agents.ClaimJobAsync(linked.Credential));
                var trust = Assert.IsType<FarmAgentTrust>(await agents.GetTrustAsync(linked.Credential));
                Assert.True(FarmManifestSecurity.TryVerify(
                    offer.ManifestPayload, offer.ManifestDigest, offer.ManifestSignature,
                    new FarmManifestSigningOptions(trust.VerificationKey), out var manifest));
                var verifiedManifest = Assert.IsType<FarmJobManifest>(manifest);
                var shardInput = Assert.IsType<FarmRemoteJobInput>(offer.Input);
                Assert.True(await agents.MarkJobStartedAsync(linked.Credential, offer.JobId));
                var execution = FarmDeterministicWorkloads.Execute(
                    verifiedManifest.Workload, verifiedManifest.Nonce,
                    verifiedManifest.Iterations, shardInput.Payload);
                var acknowledgement = await agents.SubmitResultAsync(
                    linked.Credential, offer.JobId,
                    RemoteResult(execution.Digest, 10) with { OutputPayload = execution.OutputPayload });
                Assert.True(acknowledgement?.Verified);
            }

            clock.Advance(TimeSpan.FromSeconds(6));
            var completed = await DispatchUntilAsync(
                queue, dispatcher, demand.Id, state => state.Status == FarmDemandStatus.Completed);
            Assert.Equal(FarmDemandStatus.Completed, completed.Status);
            Assert.Equal(2, completed.CompletedShards);
            Assert.Equal(64, completed.OutputDigest?.Length);
            var download = Assert.IsType<FarmUsefulJobOutput>(await queue.DownloadOutputAsync(demand.Id));
            Assert.Equal(completed.OutputDigest, Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(download.Content)).ToLowerInvariant());
            using var json = JsonDocument.Parse(download.Content);
            Assert.Equal("atos-distributed-text-index-v1", json.RootElement.GetProperty("version").GetString());
            Assert.Equal(2, json.RootElement.GetProperty("shard_count").GetInt32());
            Assert.Equal(9, json.RootElement.GetProperty("token_count").GetInt32());

            var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var jobs = await db.FarmJobs.AsNoTracking().Where(x => x.DemandId == demand.Id).ToArrayAsync();
            Assert.Equal(2, jobs.Select(x => x.NodeId).Distinct().Count());
            Assert.Equal([1, 2], jobs.OrderBy(x => x.DemandShardIndex).Select(x => x.DemandShardIndex!.Value));
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task MultiSlotAgentRunsTwoShardsWithinReservedResources()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "multi slot agent test");
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            await using var provider = BuildProvider(connectionString, identity, clock);
            await provider.InitializeAIFarmAsync();
            var lab = provider.GetRequiredService<IFarmLabService>();
            await lab.SetNodeStatusAsync(FarmNodeStatus.Paused);
            var agents = provider.GetRequiredService<IFarmAgentService>();
            var enrollment = await agents.CreateEnrollmentAsync();
            var linked = Assert.IsType<FarmAgentEnrollmentResult>(await agents.EnrollAsync(
                enrollment.Token,
                new FarmAgentEnrollmentRequest(
                    "MULTI-SLOT-A", ".NET 10 // TEST", 4, 1024L * 1024 * 1024,
                    FarmAgentProtocol.CurrentAgentVersion, "teste@atos.local")));
            await lab.ConfirmNodeAsync(linked.NodeId, linked.ConfirmationCode!);
            Assert.NotNull(await agents.HeartbeatAsync(linked.Credential,
                new FarmAgentHeartbeatRequest(
                    ".NET 10 // TEST", 4, 1024L * 1024 * 1024,
                    FarmAgentProtocol.CurrentAgentVersion, true, 2, 256L * 1024 * 1024)));

            var queue = provider.GetRequiredService<IFarmDemandQueueService>();
            var dispatcher = provider.GetRequiredService<IFarmDemandDispatcher>();
            var demand = await queue.SubmitAsync(new(
                "iris-text-index-v1", "Dois slots no mesmo Node",
                "uma máquina potente processa duas partes úteis simultaneamente",
                FarmDemandPriority.High, 60, TargetShards: 2));
            for (var attempt = 0; attempt < 5; attempt++)
            {
                _ = await dispatcher.DispatchDueAsync();
                var state = (await queue.GetDashboardAsync()).Demands.Single(x => x.Id == demand.Id);
                if (state.DispatchedShards == 2) break;
            }
            Assert.Equal(2, (await queue.GetDashboardAsync()).Demands
                .Single(x => x.Id == demand.Id).DispatchedShards);

            var first = Assert.IsType<FarmRemoteJobOffer>(
                await agents.ClaimJobAsync(linked.Credential));
            Assert.True(await agents.MarkJobStartedAsync(linked.Credential, first.JobId));
            var second = Assert.IsType<FarmRemoteJobOffer>(
                await agents.ClaimJobAsync(
                    linked.Credential, CancellationToken.None, [first.JobId]));
            Assert.NotEqual(first.JobId, second.JobId);
            Assert.True(await agents.MarkJobStartedAsync(linked.Credential, second.JobId));

            var heartbeat = Assert.IsType<FarmAgentHeartbeatResult>(await agents.HeartbeatAsync(
                linked.Credential,
                new FarmAgentHeartbeatRequest(
                    ".NET 10 // TEST", 4, 1024L * 1024 * 1024,
                    FarmAgentProtocol.CurrentAgentVersion, true, 2, 256L * 1024 * 1024,
                    first.JobId, [first.JobId, second.JobId])));
            Assert.Empty(heartbeat.CancelActiveJobIds ?? []);
            Assert.NotNull(heartbeat.LeaseExpiresAtUtc);

            var trust = Assert.IsType<FarmAgentTrust>(await agents.GetTrustAsync(linked.Credential));
            foreach (var offer in new[] { first, second })
            {
                Assert.True(FarmManifestSecurity.TryVerify(
                    offer.ManifestPayload, offer.ManifestDigest, offer.ManifestSignature,
                    new FarmManifestSigningOptions(trust.VerificationKey), out var manifest));
                var verifiedManifest = Assert.IsType<FarmJobManifest>(manifest);
                Assert.Equal(1, verifiedManifest.MaxLogicalProcessors);
                Assert.Equal(128L * 1024 * 1024, verifiedManifest.MaxMemoryBytes);
                var input = Assert.IsType<FarmRemoteJobInput>(offer.Input);
                var execution = FarmDeterministicWorkloads.Execute(
                    verifiedManifest.Workload, verifiedManifest.Nonce,
                    verifiedManifest.Iterations, input.Payload);
                Assert.True((await agents.SubmitResultAsync(
                    linked.Credential, offer.JobId,
                    RemoteResult(execution.Digest, 10) with
                    { OutputPayload = execution.OutputPayload }))?.Verified);
            }

            clock.Advance(TimeSpan.FromSeconds(6));
            for (var attempt = 0; attempt < 5; attempt++)
            {
                _ = await dispatcher.DispatchDueAsync();
                var state = (await queue.GetDashboardAsync()).Demands.Single(x => x.Id == demand.Id);
                if (state.Status == FarmDemandStatus.Completed) break;
            }
            var completed = (await queue.GetDashboardAsync()).Demands.Single(x => x.Id == demand.Id);
            Assert.Equal(FarmDemandStatus.Completed, completed.Status);
            var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var jobs = await db.FarmJobs.AsNoTracking().Where(x => x.DemandId == demand.Id).ToArrayAsync();
            Assert.Single(jobs.Select(x => x.NodeId).Distinct());
            Assert.All(jobs, job =>
            {
                Assert.Equal(1, job.ReservedLogicalProcessors);
                Assert.Equal(128L * 1024 * 1024, job.ReservedMemoryBytes);
            });
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    private static async Task<FarmDemandSnapshot> DispatchUntilAsync(
        IFarmDemandQueueService queue,
        IFarmDemandDispatcher dispatcher,
        Guid demandId,
        Func<FarmDemandSnapshot, bool> predicate)
    {
        FarmDemandSnapshot? state = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            _ = await dispatcher.DispatchDueAsync();
            state = (await queue.GetDashboardAsync()).Demands.Single(x => x.Id == demandId);
            if (predicate(state)) return state;
        }
        throw new InvalidOperationException(
            $"A demanda {demandId:N} não atingiu o estado esperado. Estado atual: {state?.Status}.");
    }

    private static ServiceProvider BuildProvider(string connectionString, AIFarmIdentity identity, TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider is not null) services.AddSingleton(timeProvider);
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(connectionString, identity);
        return services.BuildServiceProvider();
    }

    private static FarmNodeRecord Agent(
        Guid id, Guid accountId, string name, long authorizedMemory, DateTimeOffset now) => new()
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
            AgentMaxMemoryBytes = authorizedMemory,
            Runtime = ".NET 10 // test",
            LogicalProcessors = 4,
            MemoryBytes = 512L * 1024 * 1024,
            CreatedAtUtc = now,
            LastSeenAtUtc = now
        };

    private static FarmRemoteJobResult RemoteResult(string digest, long durationMilliseconds) =>
        new(digest, durationMilliseconds, 32L * 1024 * 1024, 8,
            FarmWorkerEnforcement.ProcessMonitorCpuAffinity);

    private static async Task CleanupAsync(string connectionString, Guid accountId)
    {
        await using var provider = BuildProvider(connectionString, new(accountId, accountId, "cleanup"));
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.FarmAgentEnrollments.RemoveRange(db.FarmAgentEnrollments.Where(x => x.AccountId == accountId));
        await db.SaveChangesAsync();
        var jobIds = await db.FarmJobs.Where(x => x.AccountId == accountId).Select(x => x.Id).ToArrayAsync();
        db.FarmJobEvents.RemoveRange(db.FarmJobEvents.Where(x => jobIds.Contains(x.JobId)));
        db.FarmJobs.RemoveRange(db.FarmJobs.Where(x => x.AccountId == accountId));
        await db.SaveChangesAsync();
        db.FarmDemands.RemoveRange(db.FarmDemands.Where(x => x.AccountId == accountId));
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



    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
