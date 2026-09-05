using System.Text;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using atos.AIFarm.Infrastructure.Providers.FarmNetwork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

[Collection("Farm dispatch persistence")]
public sealed class PostgresFarmNetworkRouteTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ChatQuestionIsAnsweredByTheNetworkFromTheIndexedLibrary()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "farm network route test");
        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            await provider.InitializeAIFarmAsync();
            var agents = provider.GetRequiredService<IFarmAgentService>();
            var lab = provider.GetRequiredService<IFarmLabService>();
            await lab.SetNodeStatusAsync(FarmNodeStatus.Paused);
            var linked = await EnrollConfirmedAsync(provider, "NETWORK-ANSWER-PC");
            var queue = provider.GetRequiredService<IFarmDemandQueueService>();
            var dispatcher = provider.GetRequiredService<IFarmDemandDispatcher>();

            // 1. A biblioteca nasce de uma demanda de indexação executada pelo node.
            const string corpus =
                "O credito computacional nasce do trabalho verificado pela rede. " +
                "Cada job concluido e conferido gera pontos de processamento. " +
                "O settlement converte pontos em AIC dentro do orcamento mensal.";
            var indexDemand = await queue.SubmitAsync(new FarmDemandRequest(
                "iris-text-index-v1", "Biblioteca de teste", corpus,
                FarmDemandPriority.High, 60, TargetShards: 1));
            FarmUsefulJobOutput? indexOutput = null;
            for (var round = 0; round < 40 && indexOutput is null; round++)
            {
                await RunAllPendingWorkAsync(agents, dispatcher, linked.Credential);
                indexOutput = await queue.DownloadOutputAsync(indexDemand.Id);
                if (indexOutput is null) await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
            Assert.NotNull(indexOutput);

            // 2. A rota do chat: o provider da rede embala a pergunta e espera
            //    o node computar a resposta extrativa verificada.
            var farmProvider = new FarmNetworkChatProvider(
                queue,
                provider.GetRequiredService<IFarmKnowledgeSearchService>(),
                TimeProvider.System,
                FarmNetworkOptions.Default,
                provider.GetRequiredService<IFarmLabService>());
            var request = new AiProviderRequest(
                "como nasce o credito computacional?",
                FarmNetworkAiCatalog.Route);
            var execution = farmProvider.ExecuteAsync(request, CancellationToken.None);
            for (var round = 0; round < 60 && !execution.IsCompleted; round++)
            {
                await RunAllPendingWorkAsync(agents, dispatcher, linked.Credential);
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
            var result = await execution;

            Assert.Contains("credito computacional nasce", result.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Fontes da Farm", result.Content);
            Assert.True(result.ActualCostMinor >= 1);
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    private static async Task RunAllPendingWorkAsync(
        IFarmAgentService agents,
        IFarmDemandDispatcher dispatcher,
        string credential)
    {
        _ = await dispatcher.DispatchDueAsync();
        while (await agents.ClaimJobAsync(credential) is { } offer)
        {
            Assert.True(await agents.MarkJobStartedAsync(credential, offer.JobId));
            var trust = Assert.IsType<FarmAgentTrust>(await agents.GetTrustAsync(credential));
            Assert.True(FarmManifestSecurity.TryVerify(
                offer.ManifestPayload, offer.ManifestDigest, offer.ManifestSignature,
                new FarmManifestSigningOptions(trust.VerificationKey), out var manifest));
            var execution = FarmDeterministicWorkloads.Execute(
                manifest!.Workload, manifest.Nonce, manifest.Iterations, offer.Input?.Payload);
            var acknowledgement = await agents.SubmitResultAsync(
                credential,
                offer.JobId,
                new FarmRemoteJobResult(
                    execution.Digest, 25, 32L * 1024 * 1024, 20,
                    FarmWorkerEnforcement.ProcessMonitorCpuAffinity,
                    execution.OutputPayload));
            Assert.True(acknowledgement?.Verified);
        }
        _ = await dispatcher.DispatchDueAsync();
    }

    private static async Task<FarmAgentEnrollmentResult> EnrollConfirmedAsync(
        IServiceProvider provider, string name)
    {
        var agents = provider.GetRequiredService<IFarmAgentService>();
        var enrollment = await agents.CreateEnrollmentAsync();
        var linked = Assert.IsType<FarmAgentEnrollmentResult>(await agents.EnrollAsync(
            enrollment.Token,
            new FarmAgentEnrollmentRequest(
                name, ".NET 10 // TEST", 4, 512L * 1024 * 1024,
                FarmAgentProtocol.CurrentAgentVersion, "teste@atos.local")));
        await provider.GetRequiredService<IFarmLabService>()
            .ConfirmNodeAsync(linked.NodeId, linked.ConfirmationCode!);
        Assert.NotNull(await agents.HeartbeatAsync(linked.Credential,
            new FarmAgentHeartbeatRequest(
                ".NET 10 // TEST", 4, 512L * 1024 * 1024,
                FarmAgentProtocol.CurrentAgentVersion, true, 1, 256L * 1024 * 1024)));
        return linked;
    }

    private static ServiceProvider BuildProvider(
        string connectionString, AIFarmIdentity identity)
    {
        var services = new ServiceCollection();
        services.AddLogging();
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
        db.Wallets.RemoveRange(db.Wallets.Where(x => walletIds.Contains(x.Id)));
        db.Accounts.RemoveRange(db.Accounts.Where(x => x.Id == accountId));
        await db.SaveChangesAsync();
    }
}
