using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class AIFarmDbContext(DbContextOptions<AIFarmDbContext> options)
    : IdentityDbContext<AIFarmUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<AccountRecord> Accounts => Set<AccountRecord>();
    public DbSet<WalletRecord> Wallets => Set<WalletRecord>();
    public DbSet<LedgerTransactionRecord> LedgerTransactions => Set<LedgerTransactionRecord>();
    public DbSet<LedgerEntryRecord> LedgerEntries => Set<LedgerEntryRecord>();
    public DbSet<BalanceReservationRecord> BalanceReservations => Set<BalanceReservationRecord>();
    public DbSet<ConversationRecord> Conversations => Set<ConversationRecord>();
    public DbSet<ChatPresetRecord> ChatPresets => Set<ChatPresetRecord>();
    public DbSet<MessageRecord> Messages => Set<MessageRecord>();
    public DbSet<MessageFeedbackRecord> MessageFeedbacks => Set<MessageFeedbackRecord>();
    public DbSet<ChatAttachmentRecord> ChatAttachments => Set<ChatAttachmentRecord>();
    public DbSet<AiRequestRecord> AiRequests => Set<AiRequestRecord>();
    public DbSet<AiExecutionRecord> AiExecutions => Set<AiExecutionRecord>();
    public DbSet<FarmNodeRecord> FarmNodes => Set<FarmNodeRecord>();
    public DbSet<FarmAgentEnrollmentRecord> FarmAgentEnrollments => Set<FarmAgentEnrollmentRecord>();
    public DbSet<FarmJobRecord> FarmJobs => Set<FarmJobRecord>();
    public DbSet<FarmJobEventRecord> FarmJobEvents => Set<FarmJobEventRecord>();
    public DbSet<FarmEconomicsScenarioRecord> FarmEconomicsScenarios => Set<FarmEconomicsScenarioRecord>();
    public DbSet<FarmRewardProposalRecord> FarmRewardProposals => Set<FarmRewardProposalRecord>();
    public DbSet<OperationalIncidentAcknowledgementRecord> OperationalIncidentAcknowledgements => Set<OperationalIncidentAcknowledgementRecord>();
    public DbSet<AiUsageBudgetPolicyRecord> AiUsageBudgetPolicies => Set<AiUsageBudgetPolicyRecord>();
    public DbSet<AiUsageBudgetReservationRecord> AiUsageBudgetReservations => Set<AiUsageBudgetReservationRecord>();
    public DbSet<ApiTokenRecord> ApiTokens => Set<ApiTokenRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("aifarm");

        modelBuilder.Entity<AIFarmUser>().ToTable("auth_users");
        modelBuilder.Entity<IdentityRole<Guid>>().ToTable("auth_roles");
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("auth_user_claims");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("auth_user_logins");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("auth_user_tokens");
        modelBuilder.Entity<IdentityUserRole<Guid>>().ToTable("auth_user_roles");
        modelBuilder.Entity<IdentityRoleClaim<Guid>>().ToTable("auth_role_claims");

        modelBuilder.Entity<AccountRecord>(entity =>
        {
            entity.ToTable("accounts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DisplayName).HasMaxLength(160);
            entity.Property(x => x.Status).HasMaxLength(32);
        });

        modelBuilder.Entity<WalletRecord>(entity =>
        {
            entity.ToTable("wallets");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.AccountId).IsUnique();
            entity.Property(x => x.Unit).HasMaxLength(8);
            entity.HasOne<AccountRecord>()
                .WithOne()
                .HasForeignKey<WalletRecord>(x => x.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LedgerTransactionRecord>(entity =>
        {
            entity.ToTable("ledger_transactions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WalletId, x.IdempotencyKey }).IsUnique();
            entity.Property(x => x.Type).HasMaxLength(32);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(500);
            entity.HasOne<WalletRecord>()
                .WithMany()
                .HasForeignKey(x => x.WalletId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LedgerEntryRecord>(entity =>
        {
            entity.ToTable("ledger_entries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AccountCode).HasMaxLength(160);
            entity.Property(x => x.Bucket).HasMaxLength(32);
            entity.Property(x => x.Direction).HasMaxLength(16);
            entity.HasOne<LedgerTransactionRecord>()
                .WithMany()
                .HasForeignKey(x => x.TransactionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BalanceReservationRecord>(entity =>
        {
            entity.ToTable("balance_reservations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WalletId, x.IdempotencyKey }).IsUnique();
            entity.Property(x => x.Bucket).HasMaxLength(32);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200);
            entity.HasOne<WalletRecord>()
                .WithMany()
                .HasForeignKey(x => x.WalletId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ConversationRecord>(entity =>
        {
            entity.ToTable("conversations", table =>
            {
                table.HasCheckConstraint(
                    "CK_conversations_temperature",
                    "\"Temperature\" >= 0 AND \"Temperature\" <= 2");
                table.HasCheckConstraint(
                    "CK_conversations_maximum_output_tokens",
                    "\"MaximumOutputTokens\" >= 64 AND \"MaximumOutputTokens\" <= 128000");
                table.HasCheckConstraint(
                    "CK_conversations_system_instruction_length",
                    "\"SystemInstruction\" IS NULL OR length(\"SystemInstruction\") <= 8000");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.UpdatedAtUtc });
            entity.HasIndex(x => x.ParentConversationId);
            entity.HasIndex(x => x.BranchedFromMessageId);
            entity.Property(x => x.Title).HasMaxLength(160);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.ModelPreference).HasMaxLength(120).HasDefaultValue("auto");
            entity.Property(x => x.SystemInstruction).HasColumnType("text");
            entity.Property(x => x.Temperature).HasPrecision(3, 2).HasDefaultValue(1.0m);
            entity.Property(x => x.MaximumOutputTokens).HasDefaultValue(512);
            entity.HasOne<AccountRecord>()
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ConversationRecord>()
                .WithMany()
                .HasForeignKey(x => x.ParentConversationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ChatPresetRecord>(entity =>
        {
            entity.ToTable("chat_presets", table =>
            {
                table.HasCheckConstraint(
                    "CK_chat_presets_temperature",
                    "\"Temperature\" >= 0 AND \"Temperature\" <= 2");
                table.HasCheckConstraint(
                    "CK_chat_presets_maximum_output_tokens",
                    "\"MaximumOutputTokens\" >= 64 AND \"MaximumOutputTokens\" <= 128000");
                table.HasCheckConstraint(
                    "CK_chat_presets_system_instruction_length",
                    "\"SystemInstruction\" IS NULL OR length(\"SystemInstruction\") <= 8000");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.NormalizedName }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(80);
            entity.Property(x => x.NormalizedName).HasMaxLength(80);
            entity.Property(x => x.ModelPreference).HasMaxLength(120).HasDefaultValue("auto");
            entity.Property(x => x.SystemInstruction).HasColumnType("text");
            entity.Property(x => x.Temperature).HasPrecision(3, 2).HasDefaultValue(1.0m);
            entity.Property(x => x.MaximumOutputTokens).HasDefaultValue(512);
            entity.HasOne<AccountRecord>()
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AiRequestRecord>(entity =>
        {
            entity.ToTable("ai_requests");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.IdempotencyKey }).IsUnique();
            entity.Property(x => x.Modality).HasMaxLength(32);
            entity.Property(x => x.DataClassification).HasMaxLength(32);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200);
            entity.HasOne<AccountRecord>()
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ConversationRecord>()
                .WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AiExecutionRecord>(entity =>
        {
            entity.ToTable("ai_executions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.AiRequestId).IsUnique();
            entity.Property(x => x.Provider).HasMaxLength(120);
            entity.Property(x => x.Model).HasMaxLength(120);
            entity.Property(x => x.PriceCatalogVersion).HasMaxLength(80);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.Response).HasColumnType("text");
            entity.HasOne<AiRequestRecord>()
                .WithOne()
                .HasForeignKey<AiExecutionRecord>(x => x.AiRequestId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MessageRecord>(entity =>
        {
            entity.ToTable("messages");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ConversationId, x.IdempotencyKey }).IsUnique();
            entity.Property(x => x.Role).HasMaxLength(32);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(220);
            entity.Property(x => x.Content).HasColumnType("text");
            entity.HasOne<ConversationRecord>()
                .WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AiExecutionRecord>()
                .WithMany()
                .HasForeignKey(x => x.AiExecutionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ChatAttachmentRecord>(entity =>
        {
            entity.ToTable("chat_attachments", table =>
            {
                table.HasCheckConstraint("CK_chat_attachments_size", "\"SizeBytes\" > 0");
                table.HasCheckConstraint("CK_chat_attachments_sha256", "length(\"Sha256\") = 64");
                table.HasCheckConstraint("CK_chat_attachments_status", "\"Status\" IN ('Draft', 'Attached')");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.ConversationId, x.Status });
            entity.HasIndex(x => x.MessageId);
            entity.Property(x => x.StorageKey).HasMaxLength(700);
            entity.Property(x => x.FileName).HasMaxLength(180);
            entity.Property(x => x.ContentType).HasMaxLength(120);
            entity.Property(x => x.Sha256).HasMaxLength(64);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.HasOne<AccountRecord>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ConversationRecord>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MessageRecord>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FarmNodeRecord>(entity =>
        {
            entity.ToTable("farm_nodes", table =>
            {
                table.HasCheckConstraint("CK_farm_nodes_status", "\"Status\" IN ('Online', 'Paused', 'Offline')");
                table.HasCheckConstraint("CK_farm_nodes_verification", "\"VerificationLevel\" IN ('Detected', 'Verified')");
                table.HasCheckConstraint("CK_farm_nodes_connection_kind", "\"ConnectionKind\" IN ('Embedded', 'Agent')");
                table.HasCheckConstraint(
                    "CK_farm_nodes_agent_cpu_limit",
                    "\"AgentMaxLogicalProcessors\" IS NULL OR (\"AgentMaxLogicalProcessors\" >= 1 AND \"AgentMaxLogicalProcessors\" <= \"LogicalProcessors\")");
                table.HasCheckConstraint(
                    "CK_farm_nodes_agent_memory_limit",
                    "\"AgentMaxMemoryBytes\" IS NULL OR (\"AgentMaxMemoryBytes\" >= 1 AND \"AgentMaxMemoryBytes\" <= \"MemoryBytes\")");
                table.HasCheckConstraint(
                    "CK_farm_nodes_verified_profile",
                    "(\"VerificationLevel\" = 'Detected' AND \"VerifiedProfileHash\" IS NULL AND \"ProfileVerifiedAtUtc\" IS NULL) OR " +
                    "(\"VerificationLevel\" = 'Verified' AND \"VerifiedProfileHash\" IS NOT NULL AND length(\"VerifiedProfileHash\") = 64 AND \"ProfileVerifiedAtUtc\" IS NOT NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.AccountId).IsUnique();
            entity.HasIndex(x => x.AgentCredentialHash).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(160);
            entity.Property(x => x.Status).HasMaxLength(24);
            entity.Property(x => x.VerificationLevel).HasMaxLength(24);
            entity.Property(x => x.ConnectionKind).HasMaxLength(24).HasDefaultValue("Embedded");
            entity.Property(x => x.AgentVersion).HasMaxLength(80);
            entity.Property(x => x.AgentCredentialHash).HasMaxLength(64);
            entity.Property(x => x.VerifiedProfileHash).HasMaxLength(64);
            entity.Property(x => x.AgentAcceptJobs).HasDefaultValue(true);
            entity.Property(x => x.Runtime).HasMaxLength(500);
            entity.HasOne<AccountRecord>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FarmAgentEnrollmentRecord>(entity =>
        {
            entity.ToTable("farm_agent_enrollments");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.AccountId, x.ExpiresAtUtc });
            entity.Property(x => x.TokenHash).HasMaxLength(64);
            entity.HasOne<AccountRecord>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FarmJobRecord>(entity =>
        {
            entity.ToTable("farm_jobs", table =>
            {
                table.HasCheckConstraint("CK_farm_jobs_status", "\"Status\" IN ('Queued', 'Offered', 'Accepted', 'Running', 'Uploading', 'Verifying', 'Completed', 'Failed', 'Cancelled', 'TimedOut', 'Rejected')");
                table.HasCheckConstraint("CK_farm_jobs_cu", "\"ComputeUnitsMilli\" >= 0");
                table.HasCheckConstraint(
                    "CK_farm_jobs_api_idempotency_complete",
                    "(\"ApiTokenId\" IS NULL AND \"ApiIdempotencyKey\" IS NULL) OR " +
                    "(\"ApiTokenId\" IS NOT NULL AND \"ApiIdempotencyKey\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_farm_jobs_worker_telemetry",
                    "(\"WorkerPeakMemoryBytes\" IS NULL AND \"WorkerProcessorTimeMilliseconds\" IS NULL AND \"WorkerEnforcement\" IS NULL) OR " +
                    "(\"WorkerPeakMemoryBytes\" > 0 AND \"WorkerProcessorTimeMilliseconds\" > 0 AND \"WorkerEnforcement\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_farm_jobs_agent_lease",
                    "(\"AgentLeaseRenewedAtUtc\" IS NULL AND \"AgentLeaseExpiresAtUtc\" IS NULL) OR " +
                    "(\"AgentLeaseRenewedAtUtc\" IS NOT NULL AND \"AgentLeaseExpiresAtUtc\" IS NOT NULL AND \"AgentLeaseExpiresAtUtc\" > \"AgentLeaseRenewedAtUtc\")");
                table.HasCheckConstraint(
                    "CK_farm_jobs_manifest_complete",
                    "(\"AttemptId\" IS NULL AND \"ManifestVersion\" IS NULL AND \"ManifestPayload\" IS NULL AND \"ManifestDigest\" IS NULL AND \"ManifestSignature\" IS NULL AND \"ManifestIssuedAtUtc\" IS NULL AND \"ManifestExpiresAtUtc\" IS NULL) OR " +
                    "(\"AttemptId\" IS NOT NULL AND \"ManifestVersion\" IS NOT NULL AND \"ManifestPayload\" IS NOT NULL AND \"ManifestDigest\" IS NOT NULL AND \"ManifestSignature\" IS NOT NULL AND \"ManifestIssuedAtUtc\" IS NOT NULL AND \"ManifestExpiresAtUtc\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_farm_jobs_evidence_complete",
                    "(\"EvidenceVersion\" IS NULL AND \"EvidencePayload\" IS NULL AND \"EvidenceDigest\" IS NULL AND \"EvidenceSignature\" IS NULL AND \"EvidenceKeyId\" IS NULL AND \"EvidenceCreatedAtUtc\" IS NULL) OR " +
                    "(\"EvidenceVersion\" IS NOT NULL AND \"EvidencePayload\" IS NOT NULL AND \"EvidenceDigest\" IS NOT NULL AND \"EvidenceSignature\" IS NOT NULL AND \"EvidenceKeyId\" IS NOT NULL AND \"EvidenceCreatedAtUtc\" IS NOT NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.ApiTokenId, x.ApiIdempotencyKey }).IsUnique();
            entity.HasIndex(x => x.AccountId)
                .HasDatabaseName("UX_farm_jobs_account_active")
                .IsUnique()
                .HasFilter("\"Status\" IN ('Queued', 'Offered', 'Accepted', 'Running', 'Uploading', 'Verifying')");
            entity.HasIndex(x => x.NodeId);
            entity.HasIndex(x => x.AttemptId).IsUnique();
            entity.Property(x => x.Workload).HasMaxLength(80);
            entity.Property(x => x.ApiIdempotencyKey).HasMaxLength(120);
            entity.Property(x => x.WorkloadVersion).HasMaxLength(120);
            entity.Property(x => x.VerificationPolicy).HasMaxLength(120);
            entity.Property(x => x.Nonce).HasMaxLength(64);
            entity.Property(x => x.Status).HasMaxLength(24);
            entity.Property(x => x.ExpectedDigest).HasMaxLength(64);
            entity.Property(x => x.ResultDigest).HasMaxLength(64);
            entity.Property(x => x.ManifestVersion).HasMaxLength(80);
            entity.Property(x => x.ManifestDigest).HasMaxLength(64);
            entity.Property(x => x.ManifestSignature).HasMaxLength(64);
            entity.Property(x => x.FailureReason).HasMaxLength(500);
            entity.Property(x => x.WorkerEnforcement).HasMaxLength(80);
            entity.Property(x => x.EvidenceVersion).HasMaxLength(80);
            entity.Property(x => x.EvidenceDigest).HasMaxLength(64);
            entity.Property(x => x.EvidenceSignature).HasMaxLength(64);
            entity.Property(x => x.EvidenceKeyId).HasMaxLength(32);
            entity.HasOne<AccountRecord>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<FarmNodeRecord>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FarmJobEventRecord>(entity =>
        {
            entity.ToTable("farm_job_events");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.JobId, x.Sequence }).IsUnique();
            entity.Property(x => x.Status).HasMaxLength(24);
            entity.Property(x => x.Detail).HasMaxLength(300);
            entity.HasOne<FarmJobRecord>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FarmEconomicsScenarioRecord>(entity =>
        {
            entity.ToTable("farm_economics_scenarios", table =>
            {
                table.HasCheckConstraint("CK_farm_economics_digest", "length(\"Digest\") = 64");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.CreatedAtUtc });
            entity.Property(x => x.Name).HasMaxLength(80);
            entity.Property(x => x.Version).HasMaxLength(80);
            entity.Property(x => x.InputPayload).HasColumnType("text");
            entity.Property(x => x.ResultPayload).HasColumnType("text");
            entity.Property(x => x.SourceEvidenceDigest).HasMaxLength(64);
            entity.Property(x => x.Digest).HasMaxLength(64);
            entity.HasOne<AccountRecord>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<FarmJobRecord>().WithMany().HasForeignKey(x => x.SourceJobId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FarmRewardProposalRecord>(entity =>
        {
            entity.ToTable("farm_reward_proposals", table =>
            {
                table.HasCheckConstraint("CK_farm_reward_proposals_points", "\"ProcessingPointsMilli\" > 0");
                table.HasCheckConstraint("CK_farm_reward_proposals_reward", "\"RecommendedRewardBrl\" >= 0");
                table.HasCheckConstraint("CK_farm_reward_proposals_digest", "length(\"Digest\") = 64");
                table.HasCheckConstraint(
                    "CK_farm_reward_proposals_status",
                    "\"Status\" IN ('AwaitingPolicyAndFunding')");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.AccountId, x.ScenarioId }).IsUnique();
            entity.Property(x => x.RecommendedRewardBrl).HasPrecision(18, 6);
            entity.Property(x => x.SourceEvidenceDigest).HasMaxLength(64);
            entity.Property(x => x.ScenarioDigest).HasMaxLength(64);
            entity.Property(x => x.Version).HasMaxLength(80);
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.Property(x => x.Digest).HasMaxLength(64);
            entity.HasOne<AccountRecord>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<FarmJobRecord>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<FarmEconomicsScenarioRecord>().WithMany().HasForeignKey(x => x.ScenarioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OperationalIncidentAcknowledgementRecord>(entity =>
        {
            entity.ToTable("operational_incident_acknowledgements");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.IncidentKey }).IsUnique();
            entity.Property(x => x.IncidentKey).HasMaxLength(200);
            entity.HasOne<AccountRecord>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AiUsageBudgetPolicyRecord>(entity =>
        {
            entity.ToTable("ai_usage_budget_policies", table =>
            {
                table.HasCheckConstraint("CK_ai_usage_budget_policy_limits",
                    "\"MaximumPerRequestMinor\" >= 0 AND \"DailyLimitMinor\" >= 0 AND \"MonthlyLimitMinor\" >= 0");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.AccountId).IsUnique();
            entity.HasOne<AccountRecord>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AiUsageBudgetReservationRecord>(entity =>
        {
            entity.ToTable("ai_usage_budget_reservations", table =>
            {
                table.HasCheckConstraint("CK_ai_usage_budget_reservation_amounts",
                    "\"ReservedMinor\" > 0 AND \"ActualMinor\" >= 0 AND \"ActualMinor\" <= \"ReservedMinor\"");
                table.HasCheckConstraint("CK_ai_usage_budget_reservation_status",
                    "\"Status\" IN ('Active', 'Settled', 'Released')");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.OperationId }).IsUnique();
            entity.HasIndex(x => new { x.AccountId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.AccountId, x.Status, x.ExpiresAtUtc });
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200);
            entity.Property(x => x.Status).HasMaxLength(16);
            entity.HasOne<AccountRecord>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ApiTokenRecord>(entity =>
        {
            entity.ToTable("api_tokens", table =>
            {
                table.HasCheckConstraint("CK_api_tokens_scopes", "\"Scopes\" > 0 AND \"Scopes\" <= 255");
                table.HasCheckConstraint("CK_api_tokens_expiration", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.CreatedAtUtc });
            entity.HasIndex(x => x.Prefix);
            entity.HasIndex(x => x.SecretHash).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(80);
            entity.Property(x => x.Prefix).HasMaxLength(12);
            entity.Property(x => x.SecretHash).HasMaxLength(32);
            entity.HasOne<AccountRecord>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MessageFeedbackRecord>(entity =>
        {
            entity.ToTable("message_feedback", table =>
            {
                table.HasCheckConstraint("CK_message_feedback_rating", "\"Rating\" IN ('Positive', 'Negative')");
                table.HasCheckConstraint("CK_message_feedback_reason_length", "\"Reason\" IS NULL OR length(\"Reason\") <= 500");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.MessageId).IsUnique();
            entity.HasIndex(x => new { x.AccountId, x.Rating });
            entity.Property(x => x.Rating).HasMaxLength(16);
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.HasOne<AccountRecord>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MessageRecord>().WithOne().HasForeignKey<MessageFeedbackRecord>(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class FarmNodeRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Online";
    public string VerificationLevel { get; set; } = "Detected";
    public string ConnectionKind { get; set; } = "Embedded";
    public string? AgentVersion { get; set; }
    public string? AgentCredentialHash { get; set; }
    public DateTimeOffset? AgentCredentialIssuedAtUtc { get; set; }
    public bool AgentAcceptJobs { get; set; } = true;
    public int? AgentMaxLogicalProcessors { get; set; }
    public long? AgentMaxMemoryBytes { get; set; }
    public string? VerifiedProfileHash { get; set; }
    public DateTimeOffset? ProfileVerifiedAtUtc { get; set; }
    public string Runtime { get; set; } = string.Empty;
    public int LogicalProcessors { get; set; }
    public long MemoryBytes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
}

public sealed class FarmAgentEnrollmentRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
}

public sealed class FarmJobRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid NodeId { get; set; }
    public Guid? ApiTokenId { get; set; }
    public string? ApiIdempotencyKey { get; set; }
    public string Workload { get; set; } = string.Empty;
    public string WorkloadVersion { get; set; } = string.Empty;
    public string VerificationPolicy { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string Status { get; set; } = "Queued";
    public string? ExpectedDigest { get; set; }
    public string? ResultDigest { get; set; }
    public Guid? AttemptId { get; set; }
    public string? ManifestVersion { get; set; }
    public string? ManifestPayload { get; set; }
    public string? ManifestDigest { get; set; }
    public string? ManifestSignature { get; set; }
    public DateTimeOffset? ManifestIssuedAtUtc { get; set; }
    public DateTimeOffset? ManifestExpiresAtUtc { get; set; }
    public long ComputeUnitsMilli { get; set; }
    public long? DurationMilliseconds { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public DateTimeOffset? CancellationRequestedAtUtc { get; set; }
    public DateTimeOffset? AgentLeaseRenewedAtUtc { get; set; }
    public DateTimeOffset? AgentLeaseExpiresAtUtc { get; set; }
    public long? WorkerPeakMemoryBytes { get; set; }
    public long? WorkerProcessorTimeMilliseconds { get; set; }
    public string? WorkerEnforcement { get; set; }
    public string? EvidenceVersion { get; set; }
    public string? EvidencePayload { get; set; }
    public string? EvidenceDigest { get; set; }
    public string? EvidenceSignature { get; set; }
    public string? EvidenceKeyId { get; set; }
    public DateTimeOffset? EvidenceCreatedAtUtc { get; set; }
}

public sealed class FarmJobEventRecord
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public int Sequence { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class FarmEconomicsScenarioRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string InputPayload { get; set; } = string.Empty;
    public string ResultPayload { get; set; } = string.Empty;
    public Guid? SourceJobId { get; set; }
    public string? SourceEvidenceDigest { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Digest { get; set; } = string.Empty;
}

public sealed class FarmRewardProposalRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid JobId { get; set; }
    public Guid ScenarioId { get; set; }
    public long ProcessingPointsMilli { get; set; }
    public decimal RecommendedRewardBrl { get; set; }
    public string SourceEvidenceDigest { get; set; } = string.Empty;
    public string ScenarioDigest { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Digest { get; set; } = string.Empty;
}

public sealed class OperationalIncidentAcknowledgementRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string IncidentKey { get; set; } = string.Empty;
    public DateTimeOffset AcknowledgedAtUtc { get; set; }
}

public sealed class AiUsageBudgetPolicyRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public bool IsEnabled { get; set; }
    public long MaximumPerRequestMinor { get; set; }
    public long DailyLimitMinor { get; set; }
    public long MonthlyLimitMinor { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class AiUsageBudgetReservationRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid OperationId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public long ReservedMinor { get; set; }
    public long ActualMinor { get; set; }
    public string Status { get; set; } = "Active";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? SettledAtUtc { get; set; }
}

public sealed class ApiTokenRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public byte[] SecretHash { get; set; } = [];
    public int Scopes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? LastUsedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed class AccountRecord
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class WalletRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Unit { get; set; } = "AIC";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class LedgerTransactionRecord
{
    public Guid Id { get; set; }
    public Guid WalletId { get; set; }
    public string Type { get; set; } = string.Empty;
    public Guid CorrelationId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? SettledAtUtc { get; set; }
}

public sealed class LedgerEntryRecord
{
    public Guid Id { get; set; }
    public Guid TransactionId { get; set; }
    public string AccountCode { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class BalanceReservationRecord
{
    public Guid Id { get; set; }
    public Guid WalletId { get; set; }
    public Guid OperationId { get; set; }
    public string Bucket { get; set; } = string.Empty;
    public long ReservedAmountMinor { get; set; }
    public long CapturedAmountMinor { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class AiRequestRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid OperationId { get; set; }
    public string Modality { get; set; } = "Chat";
    public string DataClassification { get; set; } = "Standard";
    public string? RequestedRoute { get; set; }
    public long MaxCostMinor { get; set; }
    public string Status { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class ConversationRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string? Title { get; set; }
    public Guid? ParentConversationId { get; set; }
    public Guid? BranchedFromMessageId { get; set; }
    public string Status { get; set; } = "Active";
    public string ModelPreference { get; set; } = "auto";
    public string? SystemInstruction { get; set; }
    public decimal Temperature { get; set; } = 1.0m;
    public int MaximumOutputTokens { get; set; } = 512;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ChatPresetRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string ModelPreference { get; set; } = "auto";
    public string? SystemInstruction { get; set; }
    public decimal Temperature { get; set; } = 1.0m;
    public int MaximumOutputTokens { get; set; } = 512;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class MessageRecord
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid OperationId { get; set; }
    public Guid? AiExecutionId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class ChatAttachmentRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? MessageId { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class MessageFeedbackRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid MessageId { get; set; }
    public string Rating { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class AiExecutionRecord
{
    public Guid Id { get; set; }
    public Guid AiRequestId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string PriceCatalogVersion { get; set; } = string.Empty;
    public long EstimatedCostMinor { get; set; }
    public long ActualCostMinor { get; set; }
    public long InputUnits { get; set; }
    public long OutputUnits { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
}
