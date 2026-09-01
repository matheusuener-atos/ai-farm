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
            Assert.NotNull(restored.Node.ProfileVerifiedAtUtc);
            Assert.Equal(64, restored.Node.VerifiedProfileHash?.Length);
            Assert.Equal(
                restored.Node.VerifiedProfileHash,
                completed.Manifest.NodeProfileHash);
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

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task AgentEnrollmentIsOneTimeAndHeartbeatExpiresOffline()
    {
        var connectionString = ReadLocalVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "atos agent protocol test");
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        try
        {
            await using var provider = BuildProvider(connectionString, identity, clock);
            await provider.InitializeAIFarmAsync();
            var lab = provider.GetRequiredService<IFarmLabService>();
            var agents = provider.GetRequiredService<IFarmAgentService>();
            Assert.Equal(FarmNodeConnectionKind.Embedded, (await lab.GetDashboardAsync()).Node.ConnectionKind);

            var enrollment = await agents.CreateEnrollmentAsync();
            var request = new FarmAgentEnrollmentRequest(
                "LAB-AGENT-01",
                ".NET 10 // WINDOWS",
                8,
                16L * 1024 * 1024 * 1024,
                FarmAgentProtocol.CurrentAgentVersion);
            var linked = await agents.EnrollAsync(enrollment.Token, request);

            Assert.NotNull(linked);
            Assert.Null(await agents.EnrollAsync(enrollment.Token, request));
            Assert.Null(await agents.HeartbeatAsync(
                "afc_invalid",
                new FarmAgentHeartbeatRequest(request.Runtime, 8, request.MemoryBytes, request.AgentVersion)));

            var heartbeat = await agents.HeartbeatAsync(
                linked.Credential,
                new FarmAgentHeartbeatRequest(request.Runtime, 8, request.MemoryBytes, request.AgentVersion));
            Assert.NotNull(heartbeat);
            Assert.True(heartbeat.AcceptJobs);

            var online = await lab.GetDashboardAsync();
            Assert.Equal(FarmNodeConnectionKind.Agent, online.Node.ConnectionKind);
            Assert.Equal(FarmNodeStatus.Online, online.Node.Status);
            Assert.Equal(FarmAgentProtocol.CurrentAgentVersion, online.Node.AgentVersion);
            Assert.True(online.Node.AcceptJobs);
            Assert.Equal(1, online.Node.AuthorizedLogicalProcessors);
            Assert.Equal(256L * 1024 * 1024, online.Node.AuthorizedMemoryBytes);

            heartbeat = await agents.HeartbeatAsync(
                linked.Credential,
                new FarmAgentHeartbeatRequest(
                    request.Runtime,
                    8,
                    request.MemoryBytes,
                    request.AgentVersion,
                    AcceptJobs: false,
                    MaxLogicalProcessors: 1,
                    MaxMemoryBytes: 128L * 1024 * 1024));
            Assert.NotNull(heartbeat);
            Assert.False(heartbeat.AcceptJobs);
            var locallyPaused = await lab.GetDashboardAsync();
            Assert.Equal(FarmNodeStatus.Online, locallyPaused.Node.Status);
            Assert.False(locallyPaused.Node.AcceptJobs);
            Assert.Equal(128L * 1024 * 1024, locallyPaused.Node.AuthorizedMemoryBytes);
            await Assert.ThrowsAsync<InvalidOperationException>(() => lab.RunBenchmarkAsync());
            Assert.Null(await agents.ClaimJobAsync(linked.Credential));

            heartbeat = await agents.HeartbeatAsync(
                linked.Credential,
                new FarmAgentHeartbeatRequest(
                    request.Runtime,
                    8,
                    request.MemoryBytes,
                    request.AgentVersion,
                    AcceptJobs: true,
                    MaxLogicalProcessors: 1,
                    MaxMemoryBytes: 128L * 1024 * 1024));
            Assert.NotNull(heartbeat);
            Assert.True(heartbeat.AcceptJobs);

            heartbeat = await agents.HeartbeatAsync(
                linked.Credential,
                new FarmAgentHeartbeatRequest(
                    request.Runtime,
                    8,
                    request.MemoryBytes,
                    "atos-agent/0.1.0",
                    AcceptJobs: true,
                    MaxLogicalProcessors: 1,
                    MaxMemoryBytes: 128L * 1024 * 1024));
            Assert.NotNull(heartbeat);
            var incompatible = await Assert.ThrowsAsync<InvalidOperationException>(
                () => lab.RunBenchmarkAsync());
            Assert.Contains(FarmAgentProtocol.CurrentAgentVersion, incompatible.Message);

            heartbeat = await agents.HeartbeatAsync(
                linked.Credential,
                new FarmAgentHeartbeatRequest(
                    request.Runtime,
                    8,
                    request.MemoryBytes,
                    request.AgentVersion,
                    AcceptJobs: true,
                    MaxLogicalProcessors: 1,
                    MaxMemoryBytes: 128L * 1024 * 1024));
            Assert.NotNull(heartbeat);

            var trust = await agents.GetTrustAsync(linked.Credential);
            Assert.NotNull(trust);
            Assert.Equal("HMAC-SHA256-NODE-V1", trust.Algorithm);
            var offered = await lab.RunBenchmarkAsync();
            Assert.Equal(FarmJobStatus.Offered, offered.Status);
            var claimed = await agents.ClaimJobAsync(linked.Credential);
            Assert.NotNull(claimed);
            Assert.True(FarmManifestSecurity.TryVerify(
                claimed.ManifestPayload,
                claimed.ManifestDigest,
                claimed.ManifestSignature,
                new FarmManifestSigningOptions(trust.VerificationKey),
                out var manifest));
            Assert.NotNull(manifest);
            Assert.True(await agents.MarkJobStartedAsync(linked.Credential, claimed.JobId));
            var digest = DeterministicBenchmark.Compute(manifest.Nonce, manifest.Iterations);
            var acknowledgement = await agents.SubmitResultAsync(
                linked.Credential,
                claimed.JobId,
                new FarmRemoteJobResult(digest, 10));
            Assert.NotNull(acknowledgement);
            Assert.True(acknowledgement.Verified);
            Assert.Equal(FarmJobStatus.Completed, acknowledgement.Status);
            var verifiedDashboard = await lab.GetDashboardAsync();
            var remote = verifiedDashboard.RecentJobs.Single(x => x.Id == offered.Id);
            Assert.Equal(FarmVerificationLevel.Verified, verifiedDashboard.Node.VerificationLevel);
            Assert.NotNull(verifiedDashboard.Node.ProfileVerifiedAtUtc);
            Assert.Equal(64, verifiedDashboard.Node.VerifiedProfileHash?.Length);
            Assert.Equal(verifiedDashboard.Node.VerifiedProfileHash, remote.Manifest?.NodeProfileHash);
            Assert.Equal(
                [FarmJobStatus.Queued, FarmJobStatus.Offered, FarmJobStatus.Accepted,
                 FarmJobStatus.Running, FarmJobStatus.Uploading, FarmJobStatus.Verifying,
                 FarmJobStatus.Completed],
                remote.Events.Select(x => x.Status).ToArray());

            heartbeat = await agents.HeartbeatAsync(
                linked.Credential,
                new FarmAgentHeartbeatRequest(
                    request.Runtime + " // UPDATED",
                    8,
                    request.MemoryBytes,
                    request.AgentVersion,
                    AcceptJobs: true,
                    MaxLogicalProcessors: 1,
                    MaxMemoryBytes: 128L * 1024 * 1024));
            Assert.NotNull(heartbeat);
            var changedProfile = await lab.GetDashboardAsync();
            Assert.Equal(FarmVerificationLevel.Detected, changedProfile.Node.VerificationLevel);
            Assert.Null(changedProfile.Node.VerifiedProfileHash);
            Assert.Null(changedProfile.Node.ProfileVerifiedAtUtc);

            var repeated = await agents.SubmitResultAsync(
                linked.Credential,
                claimed.JobId,
                new FarmRemoteJobResult(digest, 10));
            Assert.NotNull(repeated);
            Assert.True(repeated.Verified);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                agents.SubmitResultAsync(
                    linked.Credential,
                    claimed.JobId,
                    new FarmRemoteJobResult(digest, 11)));
            Assert.Null(await agents.GetTrustAsync("afc_invalid"));
            Assert.Null(await agents.ClaimJobAsync("afc_invalid"));

            var rejectedOffer = await lab.RunBenchmarkAsync();
            var rejectedClaim = await agents.ClaimJobAsync(linked.Credential);
            Assert.NotNull(rejectedClaim);
            Assert.True(await agents.MarkJobStartedAsync(linked.Credential, rejectedClaim.JobId));
            var rejected = await agents.SubmitResultAsync(
                linked.Credential,
                rejectedClaim.JobId,
                new FarmRemoteJobResult(new string('0', 64), 10));
            Assert.NotNull(rejected);
            Assert.False(rejected.Verified);
            Assert.Equal(FarmJobStatus.Failed, rejected.Status);
            Assert.Equal(0, rejected.ComputeUnitsMilli);
            Assert.Equal(
                FarmJobStatus.Failed,
                (await lab.GetDashboardAsync()).RecentJobs.Single(x => x.Id == rejectedOffer.Id).Status);

            var runtimeOffer = await lab.RunBenchmarkAsync();
            var runtimeClaim = await agents.ClaimJobAsync(linked.Credential);
            Assert.NotNull(runtimeClaim);
            Assert.True(await agents.MarkJobStartedAsync(linked.Credential, runtimeClaim.JobId));
            clock.Advance(TimeSpan.FromSeconds(31));
            heartbeat = await agents.HeartbeatAsync(
                linked.Credential,
                new FarmAgentHeartbeatRequest(request.Runtime, 8, request.MemoryBytes, request.AgentVersion));
            Assert.NotNull(heartbeat);
            var runtimeTimedOut = (await lab.GetDashboardAsync()).RecentJobs
                .Single(x => x.Id == runtimeOffer.Id);
            Assert.Equal(FarmJobStatus.TimedOut, runtimeTimedOut.Status);
            Assert.Contains("30 segundos", runtimeTimedOut.Events[^1].Detail, StringComparison.OrdinalIgnoreCase);

            var expiringOffer = await lab.RunBenchmarkAsync();
            clock.Advance(TimeSpan.FromMinutes(6));
            heartbeat = await agents.HeartbeatAsync(
                linked.Credential,
                new FarmAgentHeartbeatRequest(request.Runtime, 8, request.MemoryBytes, request.AgentVersion));
            Assert.NotNull(heartbeat);
            var afterWatchdog = await lab.GetDashboardAsync();
            var timedOut = afterWatchdog.RecentJobs.Single(x => x.Id == expiringOffer.Id);
            Assert.Equal(FarmJobStatus.TimedOut, timedOut.Status);
            Assert.Equal(FarmJobStatus.TimedOut, timedOut.Events[^1].Status);
            Assert.Equal(0, timedOut.ComputeUnitsMilli);

            var cancellable = await lab.RunBenchmarkAsync();
            var afterCancellation = await lab.CancelJobAsync(cancellable.Id);
            var cancelled = afterCancellation.RecentJobs.Single(x => x.Id == cancellable.Id);
            Assert.Equal(FarmJobStatus.Cancelled, cancelled.Status);
            Assert.Equal(FarmJobStatus.Cancelled, cancelled.Events[^1].Status);
            Assert.Equal(
                FarmJobStatus.Cancelled,
                (await lab.CancelJobAsync(cancellable.Id)).RecentJobs
                    .Single(x => x.Id == cancellable.Id).Status);

            var policyOffer = await lab.RunBenchmarkAsync();
            var policyClaim = await agents.ClaimJobAsync(linked.Credential);
            Assert.NotNull(policyClaim);
            Assert.True(await agents.RejectJobAsync(
                linked.Credential,
                policyClaim.JobId,
                new FarmRemoteJobRejection(
                    "Política local recusou o workload.",
                    FarmRemoteJobRejectionKind.Policy)));
            var policyRejected = (await lab.GetDashboardAsync()).RecentJobs
                .Single(x => x.Id == policyOffer.Id);
            Assert.Equal(FarmJobStatus.Rejected, policyRejected.Status);
            Assert.Equal(0, policyRejected.ComputeUnitsMilli);

            clock.Advance(TimeSpan.FromSeconds(91));
            var offline = await lab.GetDashboardAsync();
            Assert.Equal(FarmNodeStatus.Offline, offline.Node.Status);
            Assert.False(offline.Node.IsHeartbeatFresh);
        }
        finally
        {
            await CleanupAsync(connectionString, accountId);
        }
    }

    private static ServiceProvider BuildProvider(
        string connectionString,
        AIFarmIdentity identity,
        TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider is not null) services.AddSingleton(timeProvider);
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
        db.FarmAgentEnrollments.RemoveRange(db.FarmAgentEnrollments.Where(x => x.AccountId == accountId));
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

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan value) => current = current.Add(value);
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
