using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

[Collection("Farm dispatch persistence")]
public sealed class PostgresGenerateNetworkTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task GenerationIsAcceptedUnverifiedWithZeroCpAndFlowsToTheDemandOutput()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "generate network test");
        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            await provider.InitializeAIFarmAsync();
            var agents = provider.GetRequiredService<IFarmAgentService>();
            var lab = provider.GetRequiredService<IFarmLabService>();
            await lab.SetNodeStatusAsync(FarmNodeStatus.Paused);
            var linked = await EnrollConfirmedAsync(provider, "GENERATE-PC");
            var queue = provider.GetRequiredService<IFarmDemandQueueService>();
            var dispatcher = provider.GetRequiredService<IFarmDemandDispatcher>();

            var input = JsonSerializer.Serialize(new FarmGenerateInput(
                FarmWorkloadCatalog.GenerateInputSchema,
                "llama3.2:3b",
                "explique o ledger"), JsonOptions);
            var demand = await queue.SubmitAsync(new FarmDemandRequest(
                FarmWorkloadCatalog.GenerateId, "Chat // geração", input,
                FarmDemandPriority.High, 5, TargetShards: 1, MaxAttemptsPerShard: 2));
            FarmRemoteJobOffer? claimed = null;
            for (var round = 0; round < 30 && claimed is null; round++)
            {
                _ = await dispatcher.DispatchDueAsync();
                claimed = await agents.ClaimJobAsync(linked.Credential);
                if (claimed is null) await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
            if (claimed is null)
            {
                var debug = (await queue.GetDashboardAsync())
                    .Demands.SingleOrDefault(x => x.Id == demand.Id);
                var attempts = debug?.Shards.SelectMany(s => s.AttemptHistory)
                    .Select(a => $"[{a.Attempt}:{a.Status}:{a.FailureReason}]") ?? [];
                throw new InvalidOperationException(
                    $"DEBUG sem oferta: status={debug?.Status} detail={debug?.Detail} tentativas={string.Join(" ", attempts)}");
            }
            var offer = Assert.IsType<FarmRemoteJobOffer>(claimed);
            Assert.True(await agents.MarkJobStartedAsync(linked.Credential, offer.JobId));

            // O node simula o runtime local: devolve a geração com digest da
            // própria saída — o servidor NÃO reexecuta (UNVERIFIED_GENERATION_V1).
            var output = JsonSerializer.Serialize(new FarmGenerateResult(
                FarmWorkloadCatalog.GenerateOutputSchema,
                "llama3.2:3b",
                "O ledger usa partidas dobradas e é imutável.",
                42, 17), JsonOptions);
            var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant();
            var acknowledgement = Assert.IsType<FarmRemoteJobAcknowledgement>(
                await agents.SubmitResultAsync(
                    linked.Credential,
                    offer.JobId,
                    new FarmRemoteJobResult(
                        digest, 4_500, 512L * 1024 * 1024, 4_000,
                        "LOCAL_MODEL_RUNTIME", output)));

            Assert.Equal(FarmJobStatus.Completed, acknowledgement.Status);
            Assert.True(acknowledgement.Verified);
            Assert.Equal(0, acknowledgement.ComputeUnitsMilli);

            // Consolidação passthrough + download íntegro do output da demanda.
            // No runner a consolidação pode não fechar num único dispatch.
            FarmUsefulJobOutput? consolidated = null;
            for (var round = 0; round < 40 && consolidated is null; round++)
            {
                _ = await dispatcher.DispatchDueAsync();
                consolidated = await queue.DownloadOutputAsync(demand.Id);
                if (consolidated is null) await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
            var download = Assert.IsType<FarmUsefulJobOutput>(consolidated);
            var payload = Encoding.UTF8.GetString(download.Content);
            var result = JsonSerializer.Deserialize<FarmGenerateResult>(payload, JsonOptions)!;
            Assert.Equal("O ledger usa partidas dobradas e é imutável.", result.Answer);

            // Sem CP, nada de proposta automática nem recompensa.
            Assert.Empty(await provider.GetRequiredService<IFarmRewardProposalStore>().ListAsync());
        }
        finally { await CleanupAsync(connectionString, accountId); }
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
                FarmAgentProtocol.CurrentAgentVersion, true, 4, 8L * 1024 * 1024 * 1024)));
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
