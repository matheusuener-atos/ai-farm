using System.Text.Json;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

[Collection("Farm dispatch persistence")]
public sealed class PostgresFarmPlatformPoolTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task PlatformDemandRunsOnSharedForeignAgentAndWorkerKeepsTheCp()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var platformAccountId = Guid.NewGuid();
        var workerAccountId = Guid.NewGuid();
        var platformIdentity = new AIFarmIdentity(platformAccountId, platformAccountId, "atos plataforma");
        var workerIdentity = new AIFarmIdentity(workerAccountId, workerAccountId, "atos trabalhador");
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            await using var platform = BuildProvider(
                connectionString, platformIdentity, clock, platformAccountId);
            await using var worker = BuildProvider(
                connectionString, workerIdentity, clock, platformAccountId);
            await platform.InitializeAIFarmAsync();
            await worker.InitializeAIFarmAsync();

            // O trabalhador vincula um Agent e compartilha com a plataforma.
            var workerLab = worker.GetRequiredService<IFarmLabService>();
            var workerAgents = worker.GetRequiredService<IFarmAgentService>();
            var enrollment = await workerAgents.CreateEnrollmentAsync();
            var linked = Assert.IsType<FarmAgentEnrollmentResult>(await workerAgents.EnrollAsync(
                enrollment.Token,
                new FarmAgentEnrollmentRequest(
                    "POOL-WORKER-01", ".NET 10 // TEST", 4, 512L * 1024 * 1024,
                    FarmAgentProtocol.CurrentAgentVersion)));
            Assert.NotNull(await workerAgents.HeartbeatAsync(linked.Credential,
                new FarmAgentHeartbeatRequest(
                    ".NET 10 // TEST", 4, 512L * 1024 * 1024,
                    FarmAgentProtocol.CurrentAgentVersion, AcceptJobs: true,
                    MaxLogicalProcessors: 1, MaxMemoryBytes: 128L * 1024 * 1024)));
            var shared = await workerLab.SetNodeSharingAsync(linked.NodeId, true);
            Assert.True(shared.Nodes.Single(x => x.Id == linked.NodeId).SharePlatformWork);

            // A plataforma submete uma demanda normal; o dispatcher a leva ao
            // Agent compartilhado de OUTRA conta, e o Job nasce do trabalhador.
            var queue = platform.GetRequiredService<IFarmDemandQueueService>();
            var dispatcher = platform.GetRequiredService<IFarmDemandDispatcher>();
            var demand = await queue.SubmitAsync(new(
                "iris-text-index-v1", "PLATAFORMA // corpus público",
                "A plataforma distribui trabalho útil para computadores voluntários",
                FarmDemandPriority.High, 60, TargetShards: 1));

            var dispatched = await DispatchUntilAsync(
                queue, dispatcher, demand.Id, state => state.DispatchedShards == 1);
            Assert.Equal(FarmDemandStatus.Submitted, dispatched.Status);

            var factory = platform.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var job = await db.FarmJobs.AsNoTracking()
                    .SingleAsync(x => x.DemandId == demand.Id);
                Assert.Equal(workerAccountId, job.AccountId);
                Assert.Equal(linked.NodeId, job.NodeId);
            }

            // O Agent do trabalhador executa normalmente.
            var offer = Assert.IsType<FarmRemoteJobOffer>(
                await workerAgents.ClaimJobAsync(linked.Credential));
            var trust = Assert.IsType<FarmAgentTrust>(
                await workerAgents.GetTrustAsync(linked.Credential));
            Assert.True(FarmManifestSecurity.TryVerify(
                offer.ManifestPayload, offer.ManifestDigest, offer.ManifestSignature,
                new FarmManifestSigningOptions(trust.VerificationKey), out var manifest));
            var verifiedManifest = Assert.IsType<FarmJobManifest>(manifest);
            var shardInput = Assert.IsType<FarmRemoteJobInput>(offer.Input);
            Assert.True(await workerAgents.MarkJobStartedAsync(linked.Credential, offer.JobId));
            var execution = FarmDeterministicWorkloads.Execute(
                verifiedManifest.Workload, verifiedManifest.Nonce,
                verifiedManifest.Iterations, shardInput.Payload);
            var acknowledgement = await workerAgents.SubmitResultAsync(
                linked.Credential, offer.JobId,
                new FarmRemoteJobResult(
                    execution.Digest, 10, 32L * 1024 * 1024, 8,
                    FarmWorkerEnforcement.ProcessMonitorCpuAffinity)
                    with
                { OutputPayload = execution.OutputPayload });
            Assert.True(acknowledgement?.Verified);

            // A plataforma consolida e baixa a saída; o CP fica do trabalhador.
            clock.Advance(TimeSpan.FromSeconds(6));
            var completed = await DispatchUntilAsync(
                queue, dispatcher, demand.Id, state => state.Status == FarmDemandStatus.Completed);
            Assert.Equal(1, completed.CompletedShards);
            var download = Assert.IsType<FarmUsefulJobOutput>(
                await queue.DownloadOutputAsync(demand.Id));
            using var json = JsonDocument.Parse(download.Content);
            Assert.Equal("atos-distributed-text-index-v1",
                json.RootElement.GetProperty("version").GetString());

            var workerJob = await workerLab.GetJobAsync(offer.JobId);
            Assert.NotNull(workerJob);
            Assert.Equal(FarmJobStatus.Completed, workerJob.Status);
            Assert.Equal(2_000, workerJob.ComputeUnitsMilli);
            Assert.True(workerJob.Evidence?.IsValid);

            // Isolamento preservado: a plataforma não enxerga o Job alheio.
            var platformLab = platform.GetRequiredService<IFarmLabService>();
            Assert.Null(await platformLab.GetJobAsync(offer.JobId));

            // Sem compartilhamento, a demanda da plataforma não cruza contas.
            await workerLab.SetNodeSharingAsync(linked.NodeId, false);
            var second = await queue.SubmitAsync(new(
                "iris-text-index-v1", "PLATAFORMA // segunda demanda",
                "Sem voluntários compartilhados nada deve ser despachado",
                FarmDemandPriority.Normal, 60, TargetShards: 1));
            await dispatcher.DispatchDueAsync();
            var undispatched = (await queue.GetDashboardAsync()).Demands
                .Single(x => x.Id == second.Id);
            Assert.NotEqual(FarmDemandStatus.Submitted, undispatched.Status);
            Assert.Equal(0, undispatched.DispatchedShards);
        }
        finally
        {
            await CleanupAsync(connectionString, workerAccountId);
            await CleanupAsync(connectionString, platformAccountId);
        }
    }

    private static async Task<FarmDemandSnapshot> DispatchUntilAsync(
        IFarmDemandQueueService queue,
        IFarmDemandDispatcher dispatcher,
        Guid demandId,
        Func<FarmDemandSnapshot, bool> predicate)
    {
        FarmDemandSnapshot? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await dispatcher.DispatchDueAsync();
            last = (await queue.GetDashboardAsync()).Demands.Single(x => x.Id == demandId);
            if (predicate(last)) return last;
        }
        throw new InvalidOperationException(
            $"Demanda não atingiu o estado esperado: {last?.Status} // {last?.Detail}");
    }

    private static ServiceProvider BuildProvider(
        string connectionString,
        AIFarmIdentity identity,
        TimeProvider clock,
        Guid platformAccountId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock);
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(
            connectionString,
            identity,
            platformPool: new FarmPlatformPoolOptions(platformAccountId));
        return services.BuildServiceProvider();
    }

    private static async Task CleanupAsync(string connectionString, Guid accountId)
    {
        await using var provider = BuildProvider(
            connectionString, new(accountId, accountId, "cleanup"), TimeProvider.System, Guid.NewGuid());
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
