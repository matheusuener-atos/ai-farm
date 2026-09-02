using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

public sealed class PostgresFarmDemandQueueTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ConcurrentDispatchCreatesExactlyOneVerifiedJob()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
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

            var processed = await Task.WhenAll(
                providerA.GetRequiredService<IFarmDemandDispatcher>().DispatchDueAsync(),
                providerB.GetRequiredService<IFarmDemandDispatcher>().DispatchDueAsync());
            Assert.Equal(1, processed.Sum());

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

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task QueuedDemandCanBeCancelledAndExpired()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
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

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task CapacityFailureRequeuesAndLaterDispatchesToAgent()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
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
                    Id = agentId, AccountId = accountId, Name = "QUEUE-AGENT",
                    Status = FarmNodeStatus.Paused.ToString(),
                    VerificationLevel = FarmVerificationLevel.Detected.ToString(),
                    ConnectionKind = FarmNodeConnectionKind.Agent.ToString(),
                    AgentVersion = FarmAgentProtocol.CurrentAgentVersion,
                    AgentCredentialHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                    AgentCredentialIssuedAtUtc = clock.GetUtcNow(), AgentAcceptJobs = true,
                    AgentMaxLogicalProcessors = 1, AgentMaxMemoryBytes = 256L * 1024 * 1024,
                    Runtime = ".NET 10 // test", LogicalProcessors = 1,
                    MemoryBytes = 512L * 1024 * 1024,
                    CreatedAtUtc = clock.GetUtcNow(), LastSeenAtUtc = clock.GetUtcNow()
                });
                await db.SaveChangesAsync();
            }
            var queue = provider.GetRequiredService<IFarmDemandQueueService>();
            var dispatcher = provider.GetRequiredService<IFarmDemandDispatcher>();
            var demand = await queue.SubmitAsync(new(
                "iris-text-index-v1", "Retomar capacidade", "Fila aguarda o Agent.",
                FarmDemandPriority.Normal, 60));
            Assert.Equal(1, await dispatcher.DispatchDueAsync());
            var deferred = (await queue.GetDashboardAsync()).Demands.Single(x => x.Id == demand.Id);
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
            Assert.Equal(1, await dispatcher.DispatchDueAsync());
            var submitted = (await queue.GetDashboardAsync()).Demands.Single(x => x.Id == demand.Id);
            Assert.Equal(FarmDemandStatus.Submitted, submitted.Status);
            Assert.Equal(FarmJobStatus.Offered, submitted.JobStatus);
            Assert.Equal(2, submitted.Attempts);
        }
        finally { await CleanupAsync(connectionString, accountId); }
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

    private static async Task CleanupAsync(string connectionString, Guid accountId)
    {
        await using var provider = BuildProvider(connectionString, new(accountId, accountId, "cleanup"));
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
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

    private static string? ReadLocalVariable(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        var path = Path.Combine(FindRepositoryRoot(), ".env.local");
        if (!File.Exists(path)) return null;
        var prefix = key + "=";
        return File.ReadLines(path).FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal))?
            [prefix.Length..].Trim().Trim('"', '\'');
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "atos.AIFarm.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
