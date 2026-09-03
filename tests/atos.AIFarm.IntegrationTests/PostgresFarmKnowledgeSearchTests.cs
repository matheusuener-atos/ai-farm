using System.Text;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

[Collection("Farm dispatch persistence")]
public sealed class PostgresFarmKnowledgeSearchTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task SearchUsesOnlyIntactCompletedIndexesFromCurrentAccount()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var ownerId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        try
        {
            await using var owner = BuildProvider(connectionString, ownerId);
            await using var foreign = BuildProvider(connectionString, foreignId);
            await owner.InitializeAIFarmAsync();
            await foreign.InitializeAIFarmAsync();
            await SeedAsync(owner, ownerId, "Manual íntegro",
                "Créditos computacionais nascem do processamento verificado.", intact: true);
            await SeedAsync(owner, ownerId, "Documento adulterado",
                "Este crédito adulterado não pode aparecer.", intact: false);
            await SeedAsync(foreign, foreignId, "Documento de outra conta",
                "Créditos computacionais secretos de outra conta.", intact: true);

            var result = await owner.GetRequiredService<IFarmKnowledgeSearchService>()
                .SearchAsync("crédito computacional");

            var hit = Assert.Single(result.Hits);
            Assert.Equal("Manual íntegro", hit.Label);
            Assert.Equal(1, result.SearchedSourceCount);
            Assert.DoesNotContain(result.Hits, x => x.Label.Contains("outra conta"));
        }
        finally
        {
            await CleanupAsync(connectionString, ownerId);
            await CleanupAsync(connectionString, foreignId);
        }
    }

    private static async Task SeedAsync(
        ServiceProvider provider,
        Guid accountId,
        string label,
        string input,
        bool intact)
    {
        var inputDigest = FarmDeterministicWorkloads.ComputeInputDigest(input);
        var shard = FarmDeterministicWorkloads.Execute(
            "IRIS_TEXT_INDEX", Guid.NewGuid().ToString("N"), 1, input);
        var aggregate = FarmDistributedTextIndex.Aggregate(inputDigest,
            [(1, inputDigest, shard.Digest, shard.OutputPayload!)]);
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        db.FarmDemands.Add(new FarmDemandRecord
        {
            Id = Guid.NewGuid(), AccountId = accountId,
            WorkloadId = "iris-text-index-v1",
            WorkloadVersion = "atos-iris-text-index-v1",
            RequiredLogicalProcessors = 1, RequiredMemoryBytes = 128L * 1024 * 1024,
            TargetShards = 1, Label = label,
            InputVersion = "atos-text-input-v1", InputPayload = input,
            InputSizeBytes = Encoding.UTF8.GetByteCount(input), InputDigest = inputDigest,
            Priority = (int)FarmDemandPriority.Normal,
            Status = FarmDemandStatus.Completed.ToString(), Attempts = 1,
            NextAttemptAtUtc = now, ExpiresAtUtc = now.AddDays(1),
            OutputVersion = FarmDistributedTextIndex.Version,
            OutputPayload = aggregate.Payload,
            OutputSizeBytes = Encoding.UTF8.GetByteCount(aggregate.Payload),
            OutputDigest = intact ? aggregate.Digest : new string('0', 64),
            FinishedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();
    }

    private static ServiceProvider BuildProvider(string connectionString, Guid accountId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(connectionString,
            new AIFarmIdentity(accountId, accountId, "knowledge test"));
        return services.BuildServiceProvider();
    }

    private static async Task CleanupAsync(string connectionString, Guid accountId)
    {
        await using var provider = BuildProvider(connectionString, accountId);
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.FarmDemands.Where(x => x.AccountId == accountId).ExecuteDeleteAsync();
        var walletIds = await db.Wallets.Where(x => x.AccountId == accountId)
            .Select(x => x.Id).ToArrayAsync();
        var transactionIds = await db.LedgerTransactions.Where(x => walletIds.Contains(x.WalletId))
            .Select(x => x.Id).ToArrayAsync();
        await db.BalanceReservations.Where(x => walletIds.Contains(x.WalletId)).ExecuteDeleteAsync();
        await db.LedgerEntries.Where(x => transactionIds.Contains(x.TransactionId)).ExecuteDeleteAsync();
        await db.LedgerTransactions.Where(x => walletIds.Contains(x.WalletId)).ExecuteDeleteAsync();
        await db.Wallets.Where(x => x.AccountId == accountId).ExecuteDeleteAsync();
        await db.Accounts.Where(x => x.Id == accountId).ExecuteDeleteAsync();
    }

    private static string? ReadLocalVariable(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "atos.AIFarm.slnx")))
            directory = directory.Parent;
        var path = Path.Combine(directory?.FullName ?? Directory.GetCurrentDirectory(), ".env.local");
        if (!File.Exists(path)) return null;
        var prefix = key + "=";
        return File.ReadLines(path).FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal))?
            [prefix.Length..].Trim().Trim('"', '\'');
    }
}
