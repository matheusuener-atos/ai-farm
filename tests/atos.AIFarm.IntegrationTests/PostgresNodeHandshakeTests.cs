using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

[Collection("Farm dispatch persistence")]
public sealed class PostgresNodeHandshakeTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task PendingNodeGetsNoWorkUntilConfirmedAndRemovalKillsCredential()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "handshake test");
        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            await provider.InitializeAIFarmAsync();
            await provider.GetRequiredService<IContinuousFarmModeService>().SetAsync(true);
            var agents = provider.GetRequiredService<IFarmAgentService>();
            var lab = provider.GetRequiredService<IFarmLabService>();

            // Enroll sem e-mail (binário antigo) é recusado antes de queimar o código.
            var enrollment = await agents.CreateEnrollmentAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => agents.EnrollAsync(
                enrollment.Token, Request("HANDSHAKE-PC", email: "")));

            var linked = Assert.IsType<FarmAgentEnrollmentResult>(await agents.EnrollAsync(
                enrollment.Token, Request("HANDSHAKE-PC", email: "dono@atos.local")));
            Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}$", linked.ConfirmationCode!);

            // Pendente: heartbeat nega trabalho e nem a farm contínua fornece top-up.
            var heartbeat = Assert.IsType<FarmAgentHeartbeatResult>(
                await agents.HeartbeatAsync(linked.Credential, Heartbeat()));
            Assert.True(heartbeat.PendingConfirmation);
            Assert.False(heartbeat.AcceptJobs);
            Assert.Null(await agents.ClaimJobAsync(linked.Credential));

            // Chave errada recusa; a certa (com hífen e minúsculas) confirma.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                lab.ConfirmNodeAsync(linked.NodeId, "0000-0000"));
            var confirmed = await lab.ConfirmNodeAsync(
                linked.NodeId, linked.ConfirmationCode!.ToLowerInvariant());
            Assert.False(confirmed.Nodes.Single(x => x.Id == linked.NodeId).PendingConfirmation);

            // Confirmado: a farm contínua entrega o marathon na hora.
            var offer = Assert.IsType<FarmRemoteJobOffer>(
                await agents.ClaimJobAsync(linked.Credential));
            Assert.NotNull(offer);

            // Remoção descomissiona: some da pool e a credencial morre na hora.
            var afterRemoval = await lab.RemoveNodeAsync(linked.NodeId);
            Assert.DoesNotContain(afterRemoval.Nodes, x => x.Id == linked.NodeId);
            Assert.Null(await agents.HeartbeatAsync(linked.Credential, Heartbeat()));

            // O assento reabre: mesmo nome vira um NODE NOVO com chaves novas.
            var second = await agents.CreateEnrollmentAsync();
            var relinked = Assert.IsType<FarmAgentEnrollmentResult>(await agents.EnrollAsync(
                second.Token, Request("HANDSHAKE-PC", email: "dono@atos.local")));
            Assert.NotEqual(linked.NodeId, relinked.NodeId);
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    private static FarmAgentEnrollmentRequest Request(string name, string email) => new(
        name, ".NET 10 // TEST", 4, 512L * 1024 * 1024,
        FarmAgentProtocol.CurrentAgentVersion, email);

    private static FarmAgentHeartbeatRequest Heartbeat() => new(
        ".NET 10 // TEST", 4, 512L * 1024 * 1024,
        FarmAgentProtocol.CurrentAgentVersion, true, 1, 256L * 1024 * 1024,
        WorkerCpuPercent: 42.5, WorkerMemoryBytes: 64L * 1024 * 1024);

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
