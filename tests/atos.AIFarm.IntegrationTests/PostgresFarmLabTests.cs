using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

public sealed class PostgresFarmLabTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task BenchmarkPersistsVerifiedJobAndAppendOnlyStateEvents()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "atos farm lab test");
        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            await provider.InitializeAIFarmAsync();
            var lab = provider.GetRequiredService<IFarmLabService>();

            var detected = await lab.GetDashboardAsync();
            var completed = await lab.RunBenchmarkAsync();
            var restored = await lab.GetDashboardAsync();

            Assert.Equal(FarmVerificationLevel.Detected, detected.Node.VerificationLevel);
            Assert.Equal(FarmJobStatus.Completed, completed.Status);
            Assert.Equal(1_000, completed.ComputeUnitsMilli);
            Assert.NotNull(completed.Manifest);
            Assert.True(completed.Manifest.IsSignatureValid);
            Assert.Equal("atos-farm-manifest-v1", completed.Manifest.Version);
            Assert.Equal("DENY_ALL", completed.Manifest.NetworkPolicy);
            Assert.Equal("STANDARD", completed.Manifest.DataClassification);
            Assert.Equal(30, completed.Manifest.TimeoutSeconds);
            Assert.Equal(100_000, completed.Manifest.Iterations);
            Assert.Equal(1, completed.Manifest.MaxLogicalProcessors);
            Assert.Equal(
                [FarmJobStatus.Queued, FarmJobStatus.Running, FarmJobStatus.Verifying, FarmJobStatus.Completed],
                completed.Events.Select(x => x.Status).ToArray());
            Assert.Equal(FarmVerificationLevel.Verified, restored.Node.VerificationLevel);
            Assert.Equal(completed.Id, restored.RecentJobs[0].Id);
        }
        finally
        {
            await CleanupAsync(connectionString, accountId);
        }
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task PausedNodeRejectsNewJobs()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "atos paused farm test");
        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            await provider.InitializeAIFarmAsync();
            var lab = provider.GetRequiredService<IFarmLabService>();
            await lab.SetNodeStatusAsync(FarmNodeStatus.Paused);

            await Assert.ThrowsAsync<InvalidOperationException>(() => lab.RunBenchmarkAsync());
        }
        finally
        {
            await CleanupAsync(connectionString, accountId);
        }
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task DatabaseRejectsTwoActiveJobsForTheSameAccount()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "atos concurrent farm test");
        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            await provider.InitializeAIFarmAsync();
            var lab = provider.GetRequiredService<IFarmLabService>();
            var dashboard = await lab.GetDashboardAsync();
            var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var now = DateTimeOffset.UtcNow;
            db.FarmJobs.AddRange(
                CreateQueuedJob(accountId, dashboard.Node.Id, now),
                CreateQueuedJob(accountId, dashboard.Node.Id, now.AddTicks(1)));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            await CleanupAsync(connectionString, accountId);
        }
    }

    private static ServiceProvider BuildProvider(string connectionString, AIFarmIdentity identity)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(connectionString, identity);
        return services.BuildServiceProvider();
    }

    private static FarmJobRecord CreateQueuedJob(
        Guid accountId,
        Guid nodeId,
        DateTimeOffset createdAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        NodeId = nodeId,
        Workload = "SHA256_CHAIN",
        WorkloadVersion = "atos-sha256-chain-v1",
        VerificationPolicy = "DETERMINISTIC_REEXECUTION_V1",
        Nonce = Guid.NewGuid().ToString("N"),
        Status = FarmJobStatus.Queued.ToString(),
        CreatedAtUtc = createdAtUtc
    };

    private static async Task CleanupAsync(string connectionString, Guid accountId)
    {
        await using var provider = BuildProvider(
            connectionString,
            new AIFarmIdentity(accountId, accountId, "cleanup"));
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var jobIds = await db.FarmJobs.Where(x => x.AccountId == accountId).Select(x => x.Id).ToArrayAsync();
        db.FarmJobEvents.RemoveRange(db.FarmJobEvents.Where(x => jobIds.Contains(x.JobId)));
        db.FarmJobs.RemoveRange(db.FarmJobs.Where(x => x.AccountId == accountId));
        db.FarmNodes.RemoveRange(db.FarmNodes.Where(x => x.AccountId == accountId));
        await db.SaveChangesAsync();
        var walletIds = await db.Wallets
            .Where(x => x.AccountId == accountId)
            .Select(x => x.Id)
            .ToArrayAsync();
        var transactionIds = await db.LedgerTransactions
            .Where(x => walletIds.Contains(x.WalletId))
            .Select(x => x.Id)
            .ToArrayAsync();
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, ".env.local");
            if (File.Exists(path))
                return File.ReadLines(path)
                    .Select(line => line.Split('=', 2))
                    .Where(parts => parts.Length == 2)
                    .FirstOrDefault(parts => parts[0].Trim() == key)?[1].Trim().Trim('"');
            directory = directory.Parent;
        }
        return null;
    }
}
