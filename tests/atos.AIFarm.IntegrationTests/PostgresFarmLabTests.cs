using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace atos.AIFarm.IntegrationTests;

[Collection("Farm dispatch persistence")]
public sealed class PostgresFarmLabTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task BenchmarkPersistsVerifiedJobAndAppendOnlyStateEvents()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();

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
            Assert.NotNull(completed.Evidence);
            Assert.True(completed.Evidence.IsValid);
            Assert.Equal("atos-farm-evidence-v1", completed.Evidence.Version);
            var evidence = await lab.DownloadEvidenceAsync(completed.Id);
            Assert.NotNull(evidence);
            Assert.EndsWith(".json", evidence.FileName, StringComparison.Ordinal);
            Assert.Contains(completed.Id.ToString(), System.Text.Encoding.UTF8.GetString(evidence.Content));
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

            var merkle = await lab.RunWorkloadAsync("merkle-root-sha256-v1");
            Assert.Equal(FarmJobStatus.Completed, merkle.Status);
            Assert.Equal("MERKLE_ROOT_SHA256", merkle.Workload);
            Assert.Equal("atos-merkle-root-sha256-v1", merkle.WorkloadVersion);
            Assert.Equal(4_096, merkle.Manifest?.Iterations);
            Assert.Equal(1_000, merkle.ComputeUnitsMilli);
            Assert.Equal(64, merkle.ResultDigest?.Length);
            Assert.True(merkle.Evidence?.IsValid);

            var api = provider.GetRequiredService<IFarmApiJobService>();
            var tokenId = Guid.NewGuid();
            var submissions = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(_ => api.SubmitAsync(
                    new FarmApiJobRequest(FarmWorkloadCatalog.DefaultId),
                    tokenId,
                    "farm-api-job-0001")));
            var submittedId = Assert.Single(submissions.Select(x => x.Id).Distinct());
            var submitted = Assert.IsType<FarmJobSnapshot>(await api.GetAsync(submittedId));
            Assert.Equal(FarmJobStatus.Completed, submitted.Status);
            Assert.Equal(submittedId, submitted.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => api.SubmitAsync(
                new FarmApiJobRequest("merkle-root-sha256-v1"),
                tokenId,
                "farm-api-job-0001"));

            var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var firstEvent = await db.FarmJobEvents.SingleAsync(
                    x => x.JobId == completed.Id && x.Sequence == 1);
                firstEvent.Detail = "Lifecycle adulterado após a emissão do bundle.";
                await db.SaveChangesAsync();
            }

            var tampered = Assert.IsType<FarmJobSnapshot>(await lab.GetJobAsync(completed.Id));
            var tamperedEvidence = Assert.IsType<FarmJobEvidenceSnapshot>(tampered.Evidence);
            Assert.False(tamperedEvidence.IsValid);
            Assert.True(tamperedEvidence.Verification.SignatureValid);
            Assert.True(tamperedEvidence.Verification.ManifestValid);
            Assert.True(tamperedEvidence.Verification.JobBindingValid);
            Assert.False(tamperedEvidence.Verification.LifecycleValid);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                lab.DownloadEvidenceAsync(completed.Id));
            var audit = await provider.GetRequiredService<IAuditTrail>()
                .SearchAsync(new AuditQuery(Category: AuditCategory.Farm, PageSize: 100));
            Assert.Equal(
                AuditIntegrityStatus.Invalid,
                audit.Events.Single(x => x.Id == completed.Id).Integrity);
            var analytics = await provider.GetRequiredService<IFarmAnalyticsQuery>().GetAsync(7);
            Assert.Equal(1, analytics.IntegrityFailureCount);
            Assert.Equal(2, analytics.CompletedJobCount);
            Assert.Equal(2_000, analytics.VerifiedProcessingPointsMilli);
            var balance = await provider.GetRequiredService<IFarmBalanceQuery>().GetAsync();
            Assert.Equal(2, balance.CompletedJobs);
            Assert.Equal(2_000, balance.VerifiedComputeUnitsMilli);

            var history = provider.GetRequiredService<IFarmJobHistoryService>();
            var allJobs = await history.SearchAsync(new FarmJobHistoryQuery(PageSize: 1));
            Assert.Equal(3, allJobs.Totals.Total);
            Assert.Equal(3, allJobs.TotalPages);
            Assert.Single(allJobs.Items);
            Assert.Equal(2, allJobs.Totals.Completed);
            Assert.Equal(1, allJobs.Totals.IntegrityFailures);
            Assert.Equal(2_000, allJobs.Totals.VerifiedProcessingPointsMilli);

            var apiJobs = await history.SearchAsync(new FarmJobHistoryQuery(
                Origin: FarmJobOrigin.PersonalApi));
            Assert.Single(apiJobs.Items);
            Assert.Equal(submitted.Id, apiJobs.Items[0].Job.Id);
            var merkleJobs = await history.SearchAsync(new FarmJobHistoryQuery(
                WorkloadId: "merkle-root-sha256-v1"));
            Assert.Single(merkleJobs.Items);
            Assert.Equal(merkle.Id, merkleJobs.Items[0].Job.Id);
            var invalidJobs = await history.SearchAsync(new FarmJobHistoryQuery(
                IntegrityFailuresOnly: true));
            Assert.Single(invalidJobs.Items);
            Assert.Equal(completed.Id, invalidJobs.Items[0].Job.Id);

            var export = await history.ExportAsync(new FarmJobHistoryQuery(
                IntegrityFailuresOnly: true));
            var exportJson = Encoding.UTF8.GetString(export.Content);
            Assert.Contains("atos-farm-job-history-export-v1", exportJson);
            Assert.Contains("payloadDigest", exportJson);
            Assert.Contains(completed.Id.ToString(), exportJson);
            Assert.DoesNotContain(submitted.Id.ToString(), exportJson);
        }
        finally
        {
            await CleanupAsync(connectionString, accountId);
        }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task FarmSchedulesDispatchExactlyOnceAndPreserveAttribution()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();

        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "atos schedule test");
        var future = DateTimeOffset.UtcNow.AddYears(1);
        var start = new DateTimeOffset(future.Ticks - future.Ticks % 10, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(start);
        try
        {
            await using var provider = BuildProvider(connectionString, identity, clock);
            await provider.InitializeAIFarmAsync();
            var schedules = provider.GetRequiredService<IFarmScheduleService>();
            var dispatcher = provider.GetRequiredService<IFarmScheduleDispatcher>();

            var created = await schedules.CreateAsync(new(
                "verificação recorrente",
                FarmWorkloadCatalog.DefaultId,
                15));
            Assert.Equal(FarmScheduleStatus.Active, created.Status);
            Assert.Equal(clock.GetUtcNow(), created.NextRunAtUtc);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                schedules.CreateAsync(new(
                    "VERIFICAÇÃO RECORRENTE",
                    FarmWorkloadCatalog.DefaultId,
                    30)));

            async Task<FarmScheduleDashboard> DispatchUntilTriggersAsync(int expected)
            {
                FarmScheduleDashboard? state = null;
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    _ = await dispatcher.DispatchDueAsync();
                    state = await schedules.GetDashboardAsync();
                    if (state.RecentTriggers.Count >= expected) return state;
                }
                throw new InvalidOperationException(
                    $"A agenda {created.Id:N} não alcançou {expected} disparo(s).");
            }

            await schedules.SetStatusAsync(created.Id, FarmScheduleStatus.Paused);
            _ = await dispatcher.DispatchDueAsync();
            Assert.Empty((await schedules.GetDashboardAsync()).RecentTriggers);
            await schedules.SetStatusAsync(created.Id, FarmScheduleStatus.Active);

            _ = await Task.WhenAll(
                dispatcher.DispatchDueAsync(),
                dispatcher.DispatchDueAsync());

            var dashboard = await DispatchUntilTriggersAsync(1);
            var trigger = Assert.Single(dashboard.RecentTriggers);
            Assert.Equal(FarmScheduleTriggerStatus.Submitted, trigger.Status);
            Assert.NotNull(trigger.JobId);
            Assert.Equal(clock.GetUtcNow().AddMinutes(15), dashboard.Schedules[0].NextRunAtUtc);

            var history = await provider.GetRequiredService<IFarmJobHistoryService>()
                .SearchAsync(new FarmJobHistoryQuery(Origin: FarmJobOrigin.Scheduled));
            var scheduled = Assert.Single(history.Items);
            Assert.Equal(trigger.JobId, scheduled.Job.Id);
            Assert.Equal(FarmJobOrigin.Scheduled, scheduled.Origin);
            Assert.True(scheduled.Job.Evidence?.IsValid);

            clock.Advance(TimeSpan.FromMinutes(46));
            dashboard = await DispatchUntilTriggersAsync(2);
            Assert.Equal(2, dashboard.RecentTriggers.Count);
            Assert.Equal(
                start.AddHours(1),
                dashboard.Schedules[0].NextRunAtUtc);
        }
        finally
        {
            await CleanupAsync(connectionString, accountId);
        }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task UsefulTextJobPersistsVerifiedOutputAndRejectsTampering()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "atos useful job test");
        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            await provider.InitializeAIFarmAsync();
            var service = provider.GetRequiredService<IFarmUsefulJobService>();
            var tokenId = Guid.NewGuid();
            var request = new FarmUsefulJobRequest(
                "iris-text-index-v1", "Base de conhecimento", "Iris organiza conhecimento para a Atos.");

            var completed = await service.SubmitAsync(request, tokenId, "useful-job-0001");
            var replay = await service.SubmitAsync(request, tokenId, "useful-job-0001");
            var output = await service.DownloadOutputAsync(completed.Id);

            Assert.Equal(completed.Id, replay.Id);
            Assert.Equal(FarmJobStatus.Completed, completed.Status);
            Assert.Equal(2_000, completed.ComputeUnitsMilli);
            Assert.NotNull(completed.Useful);
            Assert.Equal(completed.Useful.InputDigest, completed.Manifest?.InputDigest);
            Assert.True(completed.Evidence?.IsValid);
            Assert.NotNull(output);
            Assert.Contains("atos-text-index-v1", Encoding.UTF8.GetString(output.Content));

            var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var record = await db.FarmJobs.SingleAsync(x => x.Id == completed.Id);
            record.OutputPayload += " ";
            await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DownloadOutputAsync(completed.Id));
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task PausedNodeRejectsNewJobs()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();

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

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task DatabasePersistsIndependentResourceReservationsForConcurrentJobs()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();

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

            await db.SaveChangesAsync();
            var active = await db.FarmJobs.AsNoTracking()
                .Where(x => x.AccountId == accountId).ToArrayAsync();
            Assert.Equal(2, active.Length);
            Assert.Equal(2, active.Sum(x => x.ReservedLogicalProcessors));
            Assert.Equal(128L * 1024 * 1024, active.Sum(x => x.ReservedMemoryBytes));
        }
        finally
        {
            await CleanupAsync(connectionString, accountId);
        }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ComputePoolDispatchesConcurrentJobsToDistinctAgents()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var accountId = Guid.NewGuid();
        var identity = new AIFarmIdentity(accountId, accountId, "atos compute pool test");
        try
        {
            await using var provider = BuildProvider(connectionString, identity);
            await provider.InitializeAIFarmAsync();
            var lab = provider.GetRequiredService<IFarmLabService>();
            var agents = provider.GetRequiredService<IFarmAgentService>();
            await lab.GetDashboardAsync();

            async Task<FarmAgentEnrollmentResult> EnrollAsync(string name)
            {
                var enrollment = await agents.CreateEnrollmentAsync();
                return Assert.IsType<FarmAgentEnrollmentResult>(await agents.EnrollAsync(
                    enrollment.Token,
                    new FarmAgentEnrollmentRequest(
                        name, ".NET 10 // WINDOWS", 8, 16L * 1024 * 1024 * 1024,
                        FarmAgentProtocol.CurrentAgentVersion)));
            }

            var first = await EnrollAsync("POOL-NODE-A");
            var second = await EnrollAsync("POOL-NODE-B");
            var heartbeat = new FarmAgentHeartbeatRequest(
                ".NET 10 // WINDOWS", 8, 16L * 1024 * 1024 * 1024,
                FarmAgentProtocol.CurrentAgentVersion);
            Assert.NotNull(await agents.HeartbeatAsync(first.Credential, heartbeat));
            Assert.NotNull(await agents.HeartbeatAsync(second.Credential, heartbeat));

            var offers = await Task.WhenAll(
                lab.RunBenchmarkAsync(),
                lab.RunWorkloadAsync("merkle-root-sha256-v1"));
            Assert.Equal(2, offers.Select(x => x.NodeId).Distinct().Count());
            Assert.All(offers, offer => Assert.Equal(FarmJobStatus.Offered, offer.Status));

            var firstClaim = Assert.IsType<FarmRemoteJobOffer>(await agents.ClaimJobAsync(first.Credential));
            var secondClaim = Assert.IsType<FarmRemoteJobOffer>(await agents.ClaimJobAsync(second.Credential));
            foreach (var pair in new[] { (first.Credential, Claim: firstClaim), (second.Credential, Claim: secondClaim) })
            {
                var trust = Assert.IsType<FarmAgentTrust>(await agents.GetTrustAsync(pair.Credential));
                Assert.True(FarmManifestSecurity.TryVerify(
                    pair.Claim.ManifestPayload, pair.Claim.ManifestDigest, pair.Claim.ManifestSignature,
                    new FarmManifestSigningOptions(trust.VerificationKey), out var manifest));
                Assert.NotNull(manifest);
                Assert.True(await agents.MarkJobStartedAsync(pair.Credential, pair.Claim.JobId));
                var digest = FarmDeterministicWorkloads.Compute(
                    manifest.Workload, manifest.Nonce, manifest.Iterations);
                var acknowledgement = await agents.SubmitResultAsync(
                    pair.Credential, pair.Claim.JobId, RemoteResult(digest, 10));
                Assert.True(acknowledgement?.Verified);
            }

            var dashboard = await lab.GetDashboardAsync();
            Assert.Equal(3, dashboard.Nodes.Count);
            Assert.Equal(2, dashboard.Nodes.Count(x => x.ConnectionKind == FarmNodeConnectionKind.Agent));
            Assert.Equal(2, dashboard.RecentJobs.Count(x => x.Status == FarmJobStatus.Completed));
        }
        finally { await CleanupAsync(connectionString, accountId); }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task AgentEnrollmentIsOneTimeAndHeartbeatExpiresOffline()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();

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
            await Assert.ThrowsAsync<ArgumentException>(() => agents.SubmitResultAsync(
                linked.Credential,
                claimed.JobId,
                new FarmRemoteJobResult(digest, 10, 32L * 1024 * 1024, 8, "UNREPORTED")));
            var acknowledgement = await agents.SubmitResultAsync(
                linked.Credential,
                claimed.JobId,
                RemoteResult(digest, 10));
            Assert.NotNull(acknowledgement);
            Assert.True(acknowledgement.Verified);
            Assert.Equal(FarmJobStatus.Completed, acknowledgement.Status);
            var verifiedDashboard = await lab.GetDashboardAsync();
            var remote = verifiedDashboard.RecentJobs.Single(x => x.Id == offered.Id);
            Assert.Equal(FarmVerificationLevel.Verified, verifiedDashboard.Node.VerificationLevel);
            Assert.NotNull(verifiedDashboard.Node.ProfileVerifiedAtUtc);
            Assert.Equal(64, verifiedDashboard.Node.VerifiedProfileHash?.Length);
            Assert.Equal(verifiedDashboard.Node.VerifiedProfileHash, remote.Manifest?.NodeProfileHash);
            Assert.Equal(32L * 1024 * 1024, remote.WorkerPeakMemoryBytes);
            Assert.Equal(8, remote.WorkerProcessorTimeMilliseconds);
            Assert.Equal(FarmWorkerEnforcement.ProcessMonitorCpuAffinity, remote.WorkerEnforcement);
            Assert.NotNull(remote.Evidence);
            Assert.True(remote.Evidence.IsValid);
            Assert.NotNull(await lab.DownloadEvidenceAsync(remote.Id));
            Assert.Equal(
                [FarmJobStatus.Queued, FarmJobStatus.Offered, FarmJobStatus.Accepted,
                 FarmJobStatus.Running, FarmJobStatus.Uploading, FarmJobStatus.Verifying,
                 FarmJobStatus.Completed],
                remote.Events.Select(x => x.Status).ToArray());

            var merkleOffer = await lab.RunWorkloadAsync("merkle-root-sha256-v1");
            var merkleClaim = Assert.IsType<FarmRemoteJobOffer>(
                await agents.ClaimJobAsync(linked.Credential));
            Assert.True(FarmManifestSecurity.TryVerify(
                merkleClaim.ManifestPayload,
                merkleClaim.ManifestDigest,
                merkleClaim.ManifestSignature,
                new FarmManifestSigningOptions(trust.VerificationKey),
                out var merkleManifest));
            Assert.NotNull(merkleManifest);
            Assert.Equal("MERKLE_ROOT_SHA256", merkleManifest.Workload);
            Assert.True(await agents.MarkJobStartedAsync(linked.Credential, merkleClaim.JobId));
            var merkleDigest = FarmDeterministicWorkloads.Compute(
                merkleManifest.Workload,
                merkleManifest.Nonce,
                merkleManifest.Iterations);
            var merkleAcknowledgement = await agents.SubmitResultAsync(
                linked.Credential,
                merkleClaim.JobId,
                RemoteResult(merkleDigest, 10));
            Assert.Equal(merkleOffer.Id, merkleAcknowledgement?.JobId);
            Assert.True(merkleAcknowledgement?.Verified);
            Assert.Equal(1_000, merkleAcknowledgement?.ComputeUnitsMilli);

            var usefulJobs = provider.GetRequiredService<IFarmUsefulJobService>();
            var usefulOffer = await usefulJobs.SubmitAsync(new FarmUsefulJobRequest(
                "iris-text-index-v1", "Índice remoto", "Iris distribui trabalho útil entre nodes."));
            var usefulClaim = Assert.IsType<FarmRemoteJobOffer>(
                await agents.ClaimJobAsync(linked.Credential));
            var usefulInput = Assert.IsType<FarmRemoteJobInput>(usefulClaim.Input);
            Assert.Equal(usefulOffer.Manifest?.InputDigest, usefulInput.Digest);
            Assert.True(FarmManifestSecurity.TryVerify(
                usefulClaim.ManifestPayload, usefulClaim.ManifestDigest, usefulClaim.ManifestSignature,
                new FarmManifestSigningOptions(trust.VerificationKey), out var usefulManifest));
            Assert.NotNull(usefulManifest);
            Assert.True(await agents.MarkJobStartedAsync(linked.Credential, usefulClaim.JobId));
            var usefulExecution = FarmDeterministicWorkloads.Execute(
                usefulManifest.Workload, usefulManifest.Nonce,
                usefulManifest.Iterations, usefulInput.Payload);
            var usefulAck = await agents.SubmitResultAsync(
                linked.Credential, usefulClaim.JobId,
                RemoteResult(usefulExecution.Digest, 10) with { OutputPayload = usefulExecution.OutputPayload });
            Assert.True(usefulAck?.Verified);
            Assert.Equal(2_000, usefulAck?.ComputeUnitsMilli);
            Assert.NotNull(await usefulJobs.DownloadOutputAsync(usefulOffer.Id));

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
                RemoteResult(digest, 10));
            Assert.NotNull(repeated);
            Assert.True(repeated.Verified);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                agents.SubmitResultAsync(
                    linked.Credential,
                    claimed.JobId,
                    RemoteResult(digest, 11)));
            Assert.Null(await agents.GetTrustAsync("afc_invalid"));
            Assert.Null(await agents.ClaimJobAsync("afc_invalid"));

            var rejectedOffer = await lab.RunBenchmarkAsync();
            var rejectedClaim = await agents.ClaimJobAsync(linked.Credential);
            Assert.NotNull(rejectedClaim);
            Assert.True(await agents.MarkJobStartedAsync(linked.Credential, rejectedClaim.JobId));
            var rejected = await agents.SubmitResultAsync(
                linked.Credential,
                rejectedClaim.JobId,
                RemoteResult(new string('0', 64), 10));
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
            heartbeat = await agents.HeartbeatAsync(
                linked.Credential,
                new FarmAgentHeartbeatRequest(
                    request.Runtime,
                    8,
                    request.MemoryBytes,
                    request.AgentVersion,
                    ActiveJobId: runtimeClaim.JobId));
            Assert.NotNull(heartbeat);
            Assert.Equal(runtimeClaim.JobId, heartbeat.ActiveJobId);
            Assert.NotNull(heartbeat.LeaseExpiresAtUtc);
            Assert.False(heartbeat.CancelActiveJob);
            clock.Advance(TimeSpan.FromSeconds(11));
            var runtimeTimedOut = (await lab.GetDashboardAsync()).RecentJobs
                .Single(x => x.Id == runtimeOffer.Id);
            Assert.Equal(FarmJobStatus.TimedOut, runtimeTimedOut.Status);
            Assert.Contains("lease", runtimeTimedOut.Events[^1].Detail, StringComparison.OrdinalIgnoreCase);

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
            var cancellableClaim = await agents.ClaimJobAsync(linked.Credential);
            Assert.NotNull(cancellableClaim);
            Assert.True(await agents.MarkJobStartedAsync(linked.Credential, cancellableClaim.JobId));
            var afterCancellation = await lab.CancelJobAsync(cancellable.Id);
            var cancellationRequested = afterCancellation.RecentJobs.Single(x => x.Id == cancellable.Id);
            Assert.Equal(FarmJobStatus.Running, cancellationRequested.Status);
            Assert.NotNull(cancellationRequested.CancellationRequestedAtUtc);
            heartbeat = await agents.HeartbeatAsync(
                linked.Credential,
                new FarmAgentHeartbeatRequest(
                    request.Runtime,
                    8,
                    request.MemoryBytes,
                    request.AgentVersion,
                    ActiveJobId: cancellableClaim.JobId));
            Assert.NotNull(heartbeat);
            Assert.True(heartbeat.CancelActiveJob);
            Assert.True(await agents.RejectJobAsync(
                linked.Credential,
                cancellableClaim.JobId,
                new FarmRemoteJobRejection(
                    "Cancelamento cooperativo confirmado.",
                    FarmRemoteJobRejectionKind.Cancelled)));
            var cancelled = (await lab.GetDashboardAsync()).RecentJobs.Single(x => x.Id == cancellable.Id);
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
        // Os cenários de falha deliberados destes testes não podem abrir a
        // quarentena do ADR 0023 (coberta em PostgresFarmNodeReliabilityTests):
        // sob relógio manual, FinishedAtUtc empata e o desempate da ordenação
        // decidiria a quarentena de forma não determinística.
        services.AddAIFarmPostgres(
            connectionString,
            identity,
            nodeReliabilityPolicy: new FarmNodeReliabilityPolicy(
                ConsecutiveFailureThreshold: 10));
        return services.BuildServiceProvider();
    }

    private static FarmRemoteJobResult RemoteResult(string digest, long durationMilliseconds) =>
        new(
            digest,
            durationMilliseconds,
            32L * 1024 * 1024,
            8,
            FarmWorkerEnforcement.ProcessMonitorCpuAffinity);

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
        db.FarmScheduleTriggers.RemoveRange(db.FarmScheduleTriggers.Where(x => x.AccountId == accountId));
        await db.SaveChangesAsync();
        var jobIds = await db.FarmJobs.Where(x => x.AccountId == accountId).Select(x => x.Id).ToArrayAsync();
        db.FarmJobEvents.RemoveRange(db.FarmJobEvents.Where(x => jobIds.Contains(x.JobId)));
        db.FarmJobs.RemoveRange(db.FarmJobs.Where(x => x.AccountId == accountId));
        await db.SaveChangesAsync();
        db.FarmSchedules.RemoveRange(db.FarmSchedules.Where(x => x.AccountId == accountId));
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

}
