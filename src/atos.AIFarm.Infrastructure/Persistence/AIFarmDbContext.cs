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
    public DbSet<MessageRecord> Messages => Set<MessageRecord>();
    public DbSet<AiRequestRecord> AiRequests => Set<AiRequestRecord>();
    public DbSet<AiExecutionRecord> AiExecutions => Set<AiExecutionRecord>();

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
            entity.ToTable("conversations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.UpdatedAtUtc });
            entity.Property(x => x.Title).HasMaxLength(160);
            entity.Property(x => x.Status).HasMaxLength(32);
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
    }
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
    public string Status { get; set; } = "Active";
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
