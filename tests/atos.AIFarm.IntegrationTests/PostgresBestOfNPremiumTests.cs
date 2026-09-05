using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using atos.AIFarm.Infrastructure.Providers.FarmNetwork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

[Collection("Farm dispatch persistence")]
public sealed class PostgresBestOfNPremiumTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task PremiumFansOutCandidatesAndSynthesizesFromTheNetwork()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "premium best-of-n test");
        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            await provider.InitializeAIFarmAsync();
            var agents = provider.GetRequiredService<IFarmAgentService>();
            var lab = provider.GetRequiredService<IFarmLabService>();
            await lab.SetNodeStatusAsync(FarmNodeStatus.Paused);
            var linked = await EnrollConfirmedAsync(provider, "PREMIUM-PC");
            var queue = provider.GetRequiredService<IFarmDemandQueueService>();
            var dispatcher = provider.GetRequiredService<IFarmDemandDispatcher>();

            // 2 candidatas + 1 juiz = 3 gerações; um "node" simulado atende todas.
            var farmProvider = new FarmNetworkChatProvider(
                queue,
                provider.GetRequiredService<IFarmKnowledgeSearchService>(),
                TimeProvider.System,
                new FarmNetworkOptions("llama3.2:3b", PremiumCandidates: 2));
            var request = new AiProviderRequest(
                "o que é o ledger?", FarmNetworkAiCatalog.PremiumRoute);

            var execution = farmProvider.ExecuteAsync(request, CancellationToken.None);
            for (var round = 0; round < 120 && !execution.IsCompleted; round++)
            {
                await ServeGenerationJobsAsync(agents, dispatcher, linked.Credential);
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
            var result = await execution;

            Assert.Contains("AI FARM PREMIUM", result.Content);
            Assert.Contains("sintetizado de 2 respostas", result.Content);
            Assert.True(result.ActualCostMinor >= 1);
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    // Simula o runtime local: atende qualquer job IRIS_GENERATE pendente
    // devolvendo uma geração determinística (a verificação é UNVERIFIED).
    private static async Task ServeGenerationJobsAsync(
        IFarmAgentService agents, IFarmDemandDispatcher dispatcher, string credential)
    {
        _ = await dispatcher.DispatchDueAsync();
        while (await agents.ClaimJobAsync(credential) is { } offer)
        {
            Assert.True(await agents.MarkJobStartedAsync(credential, offer.JobId));
            var output = JsonSerializer.Serialize(new FarmGenerateResult(
                FarmWorkloadCatalog.GenerateOutputSchema,
                "llama3.2:3b",
                "O ledger é o livro-razão imutável de partidas dobradas.",
                20, 12), JsonOptions);
            var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant();
            await agents.SubmitResultAsync(
                credential, offer.JobId,
                new FarmRemoteJobResult(
                    digest, 1_500, 512L * 1024 * 1024, 1_200,
                    "LOCAL_MODEL_RUNTIME", output));
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
                name, ".NET 10 // TEST", 8, 12L * 1024 * 1024 * 1024,
                FarmAgentProtocol.CurrentAgentVersion, "teste@atos.local")));
        await provider.GetRequiredService<IFarmLabService>()
            .ConfirmNodeAsync(linked.NodeId, linked.ConfirmationCode!);
        Assert.NotNull(await agents.HeartbeatAsync(linked.Credential,
            new FarmAgentHeartbeatRequest(
                ".NET 10 // TEST", 8, 12L * 1024 * 1024 * 1024,
                FarmAgentProtocol.CurrentAgentVersion, true, 4, 8L * 1024 * 1024 * 1024,
                SupportsGeneration: true)));
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
