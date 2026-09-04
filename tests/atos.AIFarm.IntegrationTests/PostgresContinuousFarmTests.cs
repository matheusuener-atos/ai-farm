using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

[Collection("Farm dispatch persistence")]
public sealed class PostgresContinuousFarmTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task IdleAgentClaimReceivesMarathonJobAndCooldownHoldsTheNext()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "continuous farm test");
        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            await provider.InitializeAIFarmAsync();
            await provider.GetRequiredService<IContinuousFarmModeService>().SetAsync(true);
            var agents = provider.GetRequiredService<IFarmAgentService>();
            var linked = await EnrollAsync(provider, "CONTINUOUS-PC");

            // Fila vazia + farm contínua ligada: o claim cria e oferece um
            // marathon pinado no próprio node.
            var offer = Assert.IsType<FarmRemoteJobOffer>(
                await agents.ClaimJobAsync(linked.Credential));
            var trust = Assert.IsType<FarmAgentTrust>(
                await agents.GetTrustAsync(linked.Credential));
            Assert.True(FarmManifestSecurity.TryVerify(
                offer.ManifestPayload, offer.ManifestDigest, offer.ManifestSignature,
                new FarmManifestSigningOptions(trust.VerificationKey), out var manifest));
            Assert.Equal(FarmWorkloadCatalog.ContinuousVersion, manifest!.WorkloadVersion);
            Assert.Equal(2_000_000, manifest.Iterations);

            Assert.True(await agents.MarkJobStartedAsync(linked.Credential, offer.JobId));
            var execution = FarmDeterministicWorkloads.Execute(
                manifest.Workload, manifest.Nonce, manifest.Iterations, null);
            var acknowledgement = Assert.IsType<FarmRemoteJobAcknowledgement>(
                await agents.SubmitResultAsync(
                    linked.Credential,
                    offer.JobId,
                    new FarmRemoteJobResult(
                        execution.Digest, 700, 32L * 1024 * 1024, 650,
                        FarmWorkerEnforcement.ProcessMonitorCpuAffinity)));
            Assert.True(acknowledgement.Verified);
            Assert.Equal(20_000, acknowledgement.ComputeUnitsMilli);

            // Dentro do cooldown padrão (20s/1 job) o próximo claim fica vazio.
            Assert.Null(await agents.ClaimJobAsync(linked.Credential));
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ContinuousTopUpRequiresOptInAndCurrentAgentVersion()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "continuous gate test");
        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            await provider.InitializeAIFarmAsync();
            var agents = provider.GetRequiredService<IFarmAgentService>();

            // Conta sem opt-in: ocioso continua ocioso.
            var withoutOptIn = await EnrollAsync(provider, "CONTINUOUS-OFF");
            Assert.Null(await agents.ClaimJobAsync(withoutOptIn.Credential));

            // Opt-in ligado, mas binário antigo: o top-up não acontece.
            await provider.GetRequiredService<IContinuousFarmModeService>().SetAsync(true);
            var enrollment = await agents.CreateEnrollmentAsync();
            var outdated = Assert.IsType<FarmAgentEnrollmentResult>(await agents.EnrollAsync(
                enrollment.Token,
                new FarmAgentEnrollmentRequest(
                    "CONTINUOUS-OLD", ".NET 10 // TEST", 4, 512L * 1024 * 1024,
                    "atos-agent/0.1.0", "teste@atos.local")));
            await provider.GetRequiredService<IFarmLabService>()
                .ConfirmNodeAsync(outdated.NodeId, outdated.ConfirmationCode!);
            Assert.NotNull(await agents.HeartbeatAsync(outdated.Credential,
                new FarmAgentHeartbeatRequest(
                    ".NET 10 // TEST", 4, 512L * 1024 * 1024,
                    "atos-agent/0.1.0", true, 1, 256L * 1024 * 1024)));
            Assert.Null(await agents.ClaimJobAsync(outdated.Credential));
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    private static async Task<FarmAgentEnrollmentResult> EnrollAsync(
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
