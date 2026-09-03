using System.Text;
using System.Text.Json;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace atos.AIFarm.IntegrationTests;

public sealed class PostgresAuditTrailTests
{
    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task PersonalApiTokensAreHashedScopedRevocableAndAccountIsolated()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var firstIdentity = NewIdentity("token-first");
        var secondIdentity = NewIdentity("token-second");
        try
        {
            await using var first = BuildProvider(connectionString, firstIdentity);
            await using var second = BuildProvider(connectionString, secondIdentity);
            await first.InitializeAIFarmAsync();
            await second.InitializeAIFarmAsync();
            var firstTokens = first.GetRequiredService<IApiTokenService>();
            var secondTokens = second.GetRequiredService<IApiTokenService>();
            var created = await firstTokens.CreateAsync(new ApiTokenCreateRequest(
                "integração de teste",
                ApiTokenScope.ProfileRead | ApiTokenScope.UsageRead,
                DateTimeOffset.UtcNow.AddDays(30)));

            Assert.StartsWith("iris_pat_", created.Secret);
            Assert.DoesNotContain(created.Secret, created.Token.Prefix);
            Assert.Single(await firstTokens.ListAsync());
            Assert.Empty(await secondTokens.ListAsync());
            var authenticated = await first.GetRequiredService<IApiTokenAuthenticator>()
                .AuthenticateAsync(created.Secret);
            Assert.NotNull(authenticated);
            Assert.Equal(firstIdentity.AccountId, authenticated.AccountId);
            Assert.True(authenticated.Scopes.HasFlag(ApiTokenScope.UsageRead));
            Assert.False(authenticated.Scopes.HasFlag(ApiTokenScope.AuditRead));
            var security = await first.GetRequiredService<IAuditTrail>().SearchAsync(
                new AuditQuery(Category: AuditCategory.Security));
            var issuedEvent = Assert.Single(security.Events);
            Assert.Equal("API_TOKEN", issuedEvent.Action);
            Assert.Equal("ACTIVE", issuedEvent.Status);
            Assert.Null(await first.GetRequiredService<IApiTokenAuthenticator>()
                .AuthenticateAsync(created.Secret + "x"));

            await using (var scope = first.CreateAsyncScope())
            {
                var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
                await using var db = await dbFactory.CreateDbContextAsync();
                var stored = await db.ApiTokens.SingleAsync(x => x.Id == created.Token.Id);
                Assert.Equal(32, stored.SecretHash.Length);
                Assert.NotNull(stored.LastUsedAtUtc);
            }
            Assert.False(await secondTokens.RevokeAsync(created.Token.Id));
            Assert.True(await firstTokens.RevokeAsync(created.Token.Id));
            Assert.Null(await first.GetRequiredService<IApiTokenAuthenticator>()
                .AuthenticateAsync(created.Secret));
            var revoked = await first.GetRequiredService<IAuditTrail>().SearchAsync(
                new AuditQuery(Category: AuditCategory.Security));
            Assert.Equal("REVOKED", Assert.Single(revoked.Events).Status);
        }
        finally
        {
            await CleanupAsync(connectionString, firstIdentity, secondIdentity);
        }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task IrisBudgetIsEnforcedIdempotentAndIsolatedByAccount()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var firstIdentity = NewIdentity("budget-first");
        var secondIdentity = NewIdentity("budget-second");
        try
        {
            await using var first = BuildProvider(connectionString, firstIdentity);
            await using var second = BuildProvider(connectionString, secondIdentity);
            await first.InitializeAIFarmAsync();
            await second.InitializeAIFarmAsync();
            var firstBudget = first.GetRequiredService<IAiUsageBudgetService>();
            var secondBudget = second.GetRequiredService<IAiUsageBudgetService>();
            await firstBudget.UpdatePolicyAsync(new AiUsageBudgetPolicy(true, 20_000, 30_000, 100_000));

            var operation = Guid.NewGuid();
            var reservation = await firstBudget.ReserveAsync(operation, 20_000, "budget:test");
            var replay = await firstBudget.ReserveAsync(operation, 20_000, "budget:test");
            Assert.True(reservation.IsEnforced);
            Assert.Equal(reservation, replay);
            await Assert.ThrowsAsync<AiUsageBudgetExceededException>(() =>
                firstBudget.ReserveAsync(Guid.NewGuid(), 20_000, "budget:blocked"));
            Assert.False((await secondBudget.GetSnapshotAsync()).Policy.IsEnabled);
            Assert.Equal(0, (await secondBudget.GetSnapshotAsync()).ReservedMinor);

            await firstBudget.ReleaseAsync(operation);
            Assert.Equal(0, (await firstBudget.GetSnapshotAsync()).ReservedMinor);

            await firstBudget.UpdatePolicyAsync(new AiUsageBudgetPolicy(true, 10_000, 30_000, 100_000));
            var workflow = first.GetRequiredService<ChatWorkflow>();
            var blocked = await Assert.ThrowsAsync<AiUsageBudgetExceededException>(() =>
                workflow.SendAsync("não deve alcançar o provider", "budget:workflow-blocked"));
            Assert.Contains("por tarefa", blocked.Message);
            var wallet = await first.GetRequiredService<IWalletService>().GetSnapshotAsync();
            Assert.Single(wallet.Transactions);
            Assert.Equal(1_000_000, wallet.Balance.AvailableMinor);
        }
        finally
        {
            await CleanupAsync(connectionString, firstIdentity, secondIdentity);
        }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task IncidentsAreDetectedAcknowledgedAndIsolatedByAccount()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var firstIdentity = NewIdentity("incident-first");
        var secondIdentity = NewIdentity("incident-second");
        try
        {
            await using var first = BuildProvider(connectionString, firstIdentity);
            await using var second = BuildProvider(connectionString, secondIdentity);
            await first.InitializeAIFarmAsync();
            await second.InitializeAIFarmAsync();
            var store = first.GetRequiredService<IChatStore>();
            var conversation = await store.CreateConversationAsync();
            var operationId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await store.SaveFailedAsync(
                $"incident:{operationId:N}",
                new FailedChatExecution(
                    conversation.Id, operationId,
                    new AiRoute("test-provider", "test-model", "test-v1"),
                    2_000, "Timeout", now, now.AddSeconds(30)),
                CancellationToken.None);

            var firstIncidents = first.GetRequiredService<IOperationalIncidentService>();
            var secondIncidents = second.GetRequiredService<IOperationalIncidentService>();
            var detected = await firstIncidents.SearchAsync(new IncidentQuery(
                Severity: IncidentSeverity.Critical, OpenOnly: true));
            var incident = Assert.Single(detected.Incidents, x => x.Source == IncidentSource.Iris);
            Assert.Equal("AI_EXECUTION_FAILURE", incident.Code);
            Assert.False(incident.IsAcknowledged);
            Assert.False(await secondIncidents.AcknowledgeAsync(incident.Key));
            Assert.True(await firstIncidents.AcknowledgeAsync(incident.Key));
            Assert.True(await firstIncidents.AcknowledgeAsync(incident.Key));

            var after = await firstIncidents.SearchAsync(new IncidentQuery(Search: "timeout"));
            Assert.True(Assert.Single(after.Incidents).IsAcknowledged);
            Assert.Equal(0, after.Open);
            Assert.Empty((await firstIncidents.SearchAsync(new IncidentQuery(OpenOnly: true))).Incidents);
            Assert.Empty((await secondIncidents.SearchAsync(new IncidentQuery())).Incidents);
        }
        finally
        {
            await CleanupAsync(connectionString, firstIdentity, secondIdentity);
        }
    }

    [RequiresPostgresFact]
    [Trait("Category", "Postgres")]
    public async Task AuditIsAccountScopedFilterableAndExportsDigestEnvelope()
    {
        var connectionString = PostgresTestEnvironment.GetRequiredConnectionString();
        var firstIdentity = NewIdentity("audit-first");
        var secondIdentity = NewIdentity("audit-second");

        try
        {
            await using var first = BuildProvider(connectionString, firstIdentity);
            await using var second = BuildProvider(connectionString, secondIdentity);
            await first.InitializeAIFarmAsync();
            await second.InitializeAIFarmAsync();

            var workflow = first.GetRequiredService<ChatWorkflow>();
            await workflow.SendAsync(
                "teste isolado da auditoria",
                $"audit:{firstIdentity.AccountId:N}");

            var firstAudit = first.GetRequiredService<IAuditTrail>();
            var secondAudit = second.GetRequiredService<IAuditTrail>();
            var all = await firstAudit.SearchAsync(new AuditQuery(PageSize: 100));
            var iris = await firstAudit.SearchAsync(new AuditQuery(
                Search: "atos-fake-chat-v1",
                Category: AuditCategory.Iris,
                PageSize: 100));
            var missing = await firstAudit.SearchAsync(new AuditQuery(
                Search: "conteúdo-inexistente",
                PageSize: 100));
            var secondIris = await secondAudit.SearchAsync(new AuditQuery(
                Category: AuditCategory.Iris,
                PageSize: 100));

            Assert.True(all.Totals.Ledger >= 3);
            Assert.Equal(2, all.Totals.Iris);
            Assert.Equal(0, all.Totals.IntegrityFailures);
            Assert.All(all.Events.Where(x => x.Category == AuditCategory.Ledger),
                x => Assert.Equal(AuditIntegrityStatus.Valid, x.Integrity));
            Assert.Single(iris.Events);
            Assert.Equal(AuditCategory.Iris, iris.Events[0].Category);
            Assert.Equal("AI_EXECUTION", iris.Events[0].Action);
            Assert.Empty(missing.Events);
            Assert.Empty(secondIris.Events);

            var export = await firstAudit.ExportAsync(new AuditQuery(
                Search: "atos-fake-chat-v1",
                Category: AuditCategory.Iris));
            Assert.StartsWith("atos-audit-", export.FileName);
            Assert.Equal("application/json; charset=utf-8", export.ContentType);
            using var document = JsonDocument.Parse(export.Content);
            var root = document.RootElement;
            Assert.Equal("atos-audit-export-v1", root.GetProperty("version").GetString());
            Assert.Equal(64, root.GetProperty("payload_digest").GetString()?.Length);
            var payload = root.GetProperty("payload");
            Assert.Equal(firstIdentity.AccountId, payload.GetProperty("account_id").GetGuid());
            Assert.Single(payload.GetProperty("events").EnumerateArray());
            Assert.DoesNotContain("teste isolado da auditoria", Encoding.UTF8.GetString(export.Content));
        }
        finally
        {
            await CleanupAsync(connectionString, firstIdentity, secondIdentity);
        }
    }

    private static AIFarmIdentity NewIdentity(string suffix)
    {
        var id = Guid.NewGuid();
        return new AIFarmIdentity(id, id, suffix);
    }

    private static ServiceProvider BuildProvider(string connectionString, AIFarmIdentity identity)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAIFarmDevelopment();
        services.AddAIFarmPostgres(connectionString, identity);
        return services.BuildServiceProvider();
    }

    private static async Task CleanupAsync(string connectionString, params AIFarmIdentity[] identities)
    {
        await using var provider = BuildProvider(connectionString, identities[0]);
        var factory = provider.GetRequiredService<IDbContextFactory<AIFarmDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var accountIds = identities.Select(x => x.AccountId).ToArray();
        var walletIds = identities.Select(x => x.WalletId).ToArray();
        var conversationIds = await db.Conversations.Where(x => accountIds.Contains(x.AccountId)).Select(x => x.Id).ToArrayAsync();
        var requestIds = await db.AiRequests.Where(x => accountIds.Contains(x.AccountId)).Select(x => x.Id).ToArrayAsync();
        var transactionIds = await db.LedgerTransactions.Where(x => walletIds.Contains(x.WalletId)).Select(x => x.Id).ToArrayAsync();

        db.OperationalIncidentAcknowledgements.RemoveRange(db.OperationalIncidentAcknowledgements.Where(x => accountIds.Contains(x.AccountId)));
        db.ApiTokens.RemoveRange(db.ApiTokens.Where(x => accountIds.Contains(x.AccountId)));
        db.AiUsageBudgetReservations.RemoveRange(db.AiUsageBudgetReservations.Where(x => accountIds.Contains(x.AccountId)));
        db.AiUsageBudgetPolicies.RemoveRange(db.AiUsageBudgetPolicies.Where(x => accountIds.Contains(x.AccountId)));
        db.Messages.RemoveRange(db.Messages.Where(x => conversationIds.Contains(x.ConversationId)));
        db.AiExecutions.RemoveRange(db.AiExecutions.Where(x => requestIds.Contains(x.AiRequestId)));
        db.AiRequests.RemoveRange(db.AiRequests.Where(x => accountIds.Contains(x.AccountId)));
        await db.SaveChangesAsync();
        db.Conversations.RemoveRange(db.Conversations.Where(x => accountIds.Contains(x.AccountId)));
        db.BalanceReservations.RemoveRange(db.BalanceReservations.Where(x => walletIds.Contains(x.WalletId)));
        db.LedgerEntries.RemoveRange(db.LedgerEntries.Where(x => transactionIds.Contains(x.TransactionId)));
        await db.SaveChangesAsync();
        db.LedgerTransactions.RemoveRange(db.LedgerTransactions.Where(x => walletIds.Contains(x.WalletId)));
        await db.SaveChangesAsync();
        var jobIds = await db.FarmJobs.Where(x => accountIds.Contains(x.AccountId)).Select(x => x.Id).ToArrayAsync();
        db.FarmJobEvents.RemoveRange(db.FarmJobEvents.Where(x => jobIds.Contains(x.JobId)));
        db.FarmJobs.RemoveRange(db.FarmJobs.Where(x => accountIds.Contains(x.AccountId)));
        db.FarmNodes.RemoveRange(db.FarmNodes.Where(x => accountIds.Contains(x.AccountId)));
        await db.SaveChangesAsync();
        db.Wallets.RemoveRange(db.Wallets.Where(x => walletIds.Contains(x.Id)));
        db.Accounts.RemoveRange(db.Accounts.Where(x => accountIds.Contains(x.Id)));
        await db.SaveChangesAsync();
    }

}
