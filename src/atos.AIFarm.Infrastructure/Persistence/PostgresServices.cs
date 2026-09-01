using System.Data;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresWalletService(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    TimeProvider timeProvider) : IWalletService
{
    public async Task<WalletSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await EnsureInitializedAsync(identity, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var transactionRecords = await db.LedgerTransactions
            .Where(x => x.WalletId == identity.WalletId)
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var transactionIds = transactionRecords.Select(x => x.Id).ToArray();
        var entryRecords = await db.LedgerEntries
            .Where(x => transactionIds.Contains(x.TransactionId))
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var entriesByTransaction = entryRecords
            .GroupBy(x => x.TransactionId)
            .ToDictionary(x => x.Key, x => x.Select(ToDomain).ToArray());
        var transactions = transactionRecords.Select(record => new LedgerTransaction(
            record.Id,
            record.WalletId,
            Enum.Parse<LedgerTransactionType>(record.Type),
            record.CorrelationId,
            record.IdempotencyKey,
            record.Description,
            record.CreatedAtUtc,
            entriesByTransaction.GetValueOrDefault(record.Id, []))).ToArray();

        var availableByBucket = Enum.GetValues<CreditBucket>().ToDictionary(
            bucket => bucket,
            bucket => BalanceFor(entryRecords, AvailableAccount(identity, bucket)));
        var reserved = Enum.GetValues<CreditBucket>()
            .Sum(bucket => BalanceFor(entryRecords, ReservedAccount(identity, bucket)));
        return new WalletSnapshot(
            identity.WalletId,
            new WalletBalance(availableByBucket.Values.Sum(), reserved, availableByBucket),
            transactions);
    }

    public async Task GrantPromotionalAsync(
        long amountMinor,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        EnsurePositive(amountMinor);
        await using var db = await OpenLockedAsync(identity, cancellationToken);
        if (await db.LedgerTransactions.AnyAsync(
                x => x.WalletId == identity.WalletId &&
                     x.IdempotencyKey == idempotencyKey,
                cancellationToken))
        {
            await db.Database.CommitTransactionAsync(cancellationToken);
            return;
        }

        AddBalancedTransaction(
            db,
            identity,
            LedgerTransactionType.Grant,
            Guid.NewGuid(),
            idempotencyKey,
            "Crédito promocional de desenvolvimento",
            timeProvider.GetUtcNow(),
            (AvailableAccount(identity, CreditBucket.Promotional), CreditBucket.Promotional, LedgerDirection.Debit, amountMinor),
            ("atos:PROMOTIONAL_POOL", CreditBucket.Promotional, LedgerDirection.Credit, amountMinor));
        await SaveAndCommitAsync(db, cancellationToken);
    }

    public async Task<ReservationResult> ReservePromotionalAsync(
        Guid operationId,
        long amountMinor,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        EnsurePositive(amountMinor);
        await using var db = await OpenLockedAsync(identity, cancellationToken);
        var existing = await db.BalanceReservations.SingleOrDefaultAsync(
            x => x.WalletId == identity.WalletId &&
                 x.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            await db.Database.CommitTransactionAsync(cancellationToken);
            return new ReservationResult(existing.Id, existing.ReservedAmountMinor);
        }

        var availableCode = AvailableAccount(identity, CreditBucket.Promotional);
        var available = await (
            from entry in db.LedgerEntries
            join transaction in db.LedgerTransactions on entry.TransactionId equals transaction.Id
            where transaction.WalletId == identity.WalletId &&
                  entry.AccountCode == availableCode
            select entry.Direction == nameof(LedgerDirection.Debit)
                ? entry.AmountMinor
                : -entry.AmountMinor).SumAsync(cancellationToken);
        if (available < amountMinor)
        {
            throw new InsufficientBalanceException(available, amountMinor);
        }

        var now = timeProvider.GetUtcNow();
        var reservation = new BalanceReservationRecord
        {
            Id = Guid.NewGuid(),
            WalletId = identity.WalletId,
            OperationId = operationId,
            Bucket = nameof(CreditBucket.Promotional),
            ReservedAmountMinor = amountMinor,
            Status = nameof(ReservationStatus.Active),
            ExpiresAtUtc = now.AddMinutes(5),
            IdempotencyKey = idempotencyKey
        };
        db.BalanceReservations.Add(reservation);
        AddBalancedTransaction(
            db,
            identity,
            LedgerTransactionType.Reserve,
            operationId,
            $"{idempotencyKey}:ledger",
            "Reserva para execução de IA",
            now,
            (ReservedAccount(identity, CreditBucket.Promotional), CreditBucket.Promotional, LedgerDirection.Debit, amountMinor),
            (availableCode, CreditBucket.Promotional, LedgerDirection.Credit, amountMinor));
        await SaveAndCommitAsync(db, cancellationToken);
        return new ReservationResult(reservation.Id, amountMinor);
    }

    public async Task CaptureAsync(
        Guid reservationId,
        long actualAmountMinor,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await OpenLockedAsync(identity, cancellationToken);
        if (await TransactionExistsAsync(db, identity, idempotencyKey, cancellationToken))
        {
            await db.Database.CommitTransactionAsync(cancellationToken);
            return;
        }

        var reservation = await db.BalanceReservations.SingleAsync(
            x => x.Id == reservationId && x.WalletId == identity.WalletId,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (reservation.Status != nameof(ReservationStatus.Active) ||
            reservation.ExpiresAtUtc <= now ||
            actualAmountMinor <= 0 ||
            actualAmountMinor > reservation.ReservedAmountMinor)
        {
            throw new InvalidOperationException("A reserva não pode ser capturada.");
        }

        var bucket = Enum.Parse<CreditBucket>(reservation.Bucket);
        AddBalancedTransaction(
            db,
            identity,
            LedgerTransactionType.Capture,
            reservation.OperationId,
            idempotencyKey,
            "Liquidação do custo real de IA",
            now,
            ("atos:AI_CONSUMPTION", bucket, LedgerDirection.Debit, actualAmountMinor),
            (ReservedAccount(identity, bucket), bucket, LedgerDirection.Credit, actualAmountMinor));

        var remainder = reservation.ReservedAmountMinor - actualAmountMinor;
        if (remainder > 0)
        {
            AddBalancedTransaction(
                db,
                identity,
                LedgerTransactionType.Release,
                reservation.OperationId,
                $"{idempotencyKey}:remainder",
                "Liberação da diferença entre estimativa e custo real",
                now,
                (AvailableAccount(identity, bucket), bucket, LedgerDirection.Debit, remainder),
                (ReservedAccount(identity, bucket), bucket, LedgerDirection.Credit, remainder));
        }

        reservation.CapturedAmountMinor = actualAmountMinor;
        reservation.Status = nameof(ReservationStatus.Captured);
        await SaveAndCommitAsync(db, cancellationToken);
    }

    public async Task ReleaseAsync(
        Guid reservationId,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await OpenLockedAsync(identity, cancellationToken);
        if (await TransactionExistsAsync(db, identity, idempotencyKey, cancellationToken))
        {
            await db.Database.CommitTransactionAsync(cancellationToken);
            return;
        }

        var reservation = await db.BalanceReservations.SingleAsync(
            x => x.Id == reservationId && x.WalletId == identity.WalletId,
            cancellationToken);
        if (reservation.Status is nameof(ReservationStatus.Released) or nameof(ReservationStatus.Expired))
        {
            await db.Database.CommitTransactionAsync(cancellationToken);
            return;
        }

        if (reservation.Status != nameof(ReservationStatus.Active))
        {
            throw new InvalidOperationException("Somente uma reserva ativa pode ser liberada.");
        }

        var bucket = Enum.Parse<CreditBucket>(reservation.Bucket);
        AddBalancedTransaction(
            db,
            identity,
            LedgerTransactionType.Release,
            reservation.OperationId,
            idempotencyKey,
            reason,
            timeProvider.GetUtcNow(),
            (AvailableAccount(identity, bucket), bucket, LedgerDirection.Debit, reservation.ReservedAmountMinor),
            (ReservedAccount(identity, bucket), bucket, LedgerDirection.Credit, reservation.ReservedAmountMinor));
        reservation.Status = nameof(ReservationStatus.Released);
        await SaveAndCommitAsync(db, cancellationToken);
    }

    public async Task<int> ExpireReservationsAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await OpenLockedAsync(identity, cancellationToken);
        var expired = await db.BalanceReservations
            .Where(x =>
                x.WalletId == identity.WalletId &&
                x.Status == nameof(ReservationStatus.Active) &&
                x.ExpiresAtUtc <= utcNow)
            .OrderBy(x => x.ExpiresAtUtc)
            .ToListAsync(cancellationToken);

        foreach (var reservation in expired)
        {
            var bucket = Enum.Parse<CreditBucket>(reservation.Bucket);
            AddBalancedTransaction(
                db,
                identity,
                LedgerTransactionType.Release,
                reservation.OperationId,
                $"reservation:{reservation.Id:N}:expiry",
                "Liberação automática de reserva expirada",
                utcNow,
                (AvailableAccount(identity, bucket), bucket, LedgerDirection.Debit, reservation.ReservedAmountMinor),
                (ReservedAccount(identity, bucket), bucket, LedgerDirection.Credit, reservation.ReservedAmountMinor));
            reservation.Status = nameof(ReservationStatus.Expired);
        }

        await SaveAndCommitAsync(db, cancellationToken);
        return expired.Count;
    }

    private async Task EnsureInitializedAsync(
        AIFarmIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.Accounts.AnyAsync(x => x.Id == identity.AccountId, cancellationToken))
        {
            db.Accounts.Add(new AccountRecord
            {
                Id = identity.AccountId,
                DisplayName = identity.DisplayName,
                Status = "Active",
                CreatedAtUtc = timeProvider.GetUtcNow()
            });
            db.Wallets.Add(new WalletRecord
            {
                Id = identity.WalletId,
                AccountId = identity.AccountId,
                Unit = "AIC",
                CreatedAtUtc = timeProvider.GetUtcNow()
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<AIFarmDbContext> OpenLockedAsync(
        AIFarmIdentity identity,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(identity, cancellationToken);
        var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        _ = await db.Wallets.FromSqlInterpolated(
                $"SELECT * FROM aifarm.wallets WHERE \"Id\" = {identity.WalletId} FOR UPDATE")
            .SingleAsync(cancellationToken);
        return db;
    }

    private static async Task SaveAndCommitAsync(
        AIFarmDbContext db,
        CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        await db.Database.CommitTransactionAsync(cancellationToken);
    }

    private Task<bool> TransactionExistsAsync(
        AIFarmDbContext db,
        AIFarmIdentity identity,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        db.LedgerTransactions.AnyAsync(
            x => x.WalletId == identity.WalletId &&
                 x.IdempotencyKey == idempotencyKey,
            cancellationToken);

    private void AddBalancedTransaction(
        AIFarmDbContext db,
        AIFarmIdentity identity,
        LedgerTransactionType type,
        Guid correlationId,
        string idempotencyKey,
        string description,
        DateTimeOffset now,
        params (string Account, CreditBucket Bucket, LedgerDirection Direction, long Amount)[] entries)
    {
        if (entries.Sum(x => x.Direction == LedgerDirection.Debit ? x.Amount : -x.Amount) != 0)
        {
            throw new InvalidOperationException("Todo lançamento deve ser balanceado.");
        }

        var transactionId = Guid.NewGuid();
        db.LedgerTransactions.Add(new LedgerTransactionRecord
        {
            Id = transactionId,
            WalletId = identity.WalletId,
            Type = type.ToString(),
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            Description = description,
            CreatedAtUtc = now,
            SettledAtUtc = now
        });
        db.LedgerEntries.AddRange(entries.Select(entry => new LedgerEntryRecord
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            AccountCode = entry.Account,
            Bucket = entry.Bucket.ToString(),
            Direction = entry.Direction.ToString(),
            AmountMinor = entry.Amount,
            CreatedAtUtc = now
        }));
    }

    private static LedgerEntry ToDomain(LedgerEntryRecord entry) => new(
        entry.Id,
        entry.AccountCode,
        Enum.Parse<CreditBucket>(entry.Bucket),
        Enum.Parse<LedgerDirection>(entry.Direction),
        entry.AmountMinor,
        entry.CreatedAtUtc);

    private static long BalanceFor(IEnumerable<LedgerEntryRecord> entries, string account) =>
        entries.Where(x => x.AccountCode == account)
            .Sum(x => x.Direction == nameof(LedgerDirection.Debit)
                ? x.AmountMinor
                : -x.AmountMinor);

    private static string AvailableAccount(AIFarmIdentity identity, CreditBucket bucket) =>
        $"WALLET:{identity.WalletId}:{bucket}:AVAILABLE";

    private static string ReservedAccount(AIFarmIdentity identity, CreditBucket bucket) =>
        $"WALLET:{identity.WalletId}:{bucket}:RESERVED";

    private static void EnsurePositive(long amountMinor)
    {
        if (amountMinor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountMinor));
        }
    }
}

public sealed class PostgresChatStore(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor)
    : IChatStore
{
    public async Task<ConversationPage> SearchConversationsAsync(
        ConversationQuery query,
        CancellationToken cancellationToken = default)
    {
        var normalized = query.Normalize();
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var status = normalized.Archived ? "Archived" : "Active";
        var conversations = db.Conversations.Where(
            x => x.AccountId == identity.AccountId && x.Status == status);
        if (normalized.Search is not null)
        {
            var pattern = $"%{EscapeLikePattern(normalized.Search)}%";
            conversations = conversations.Where(conversation =>
                (conversation.Title != null &&
                 EF.Functions.ILike(conversation.Title, pattern, @"\")) ||
                db.Messages.Any(message =>
                    message.ConversationId == conversation.Id &&
                    EF.Functions.ILike(message.Content, pattern, @"\")));
        }

        var totalCount = await conversations.CountAsync(cancellationToken);
        var items = await conversations
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip((normalized.Page - 1) * normalized.PageSize)
            .Take(normalized.PageSize)
            .Select(x => new ConversationSummary(
                x.Id,
                x.Title,
                x.CreatedAtUtc,
                x.UpdatedAtUtc,
                db.Messages.Count(message => message.ConversationId == x.Id),
                db.Messages
                    .Where(message => message.ConversationId == x.Id)
                    .OrderByDescending(message => message.CreatedAtUtc)
                    .Select(message => message.Content)
                    .FirstOrDefault(),
                x.ParentConversationId))
            .ToArrayAsync(cancellationToken);
        return new ConversationPage(
            items,
            totalCount,
            normalized.Page,
            normalized.PageSize);
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Conversations
            .Where(x => x.AccountId == identity.AccountId && x.Status == "Active")
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => new ConversationSummary(
                x.Id,
                x.Title,
                x.CreatedAtUtc,
                x.UpdatedAtUtc,
                db.Messages.Count(message => message.ConversationId == x.Id),
                db.Messages
                    .Where(message => message.ConversationId == x.Id)
                    .OrderByDescending(message => message.CreatedAtUtc)
                    .Select(message => message.Content)
                    .FirstOrDefault(),
                x.ParentConversationId))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListArchivedConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Conversations
            .Where(x => x.AccountId == identity.AccountId && x.Status == "Archived")
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => new ConversationSummary(
                x.Id,
                x.Title,
                x.CreatedAtUtc,
                x.UpdatedAtUtc,
                db.Messages.Count(message => message.ConversationId == x.Id),
                db.Messages
                    .Where(message => message.ConversationId == x.Id)
                    .OrderByDescending(message => message.CreatedAtUtc)
                    .Select(message => message.Content)
                    .FirstOrDefault(),
                x.ParentConversationId))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<ConversationSnapshot> CreateConversationAsync(
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var conversation = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            AccountId = identity.AccountId,
            Status = "Active",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return await LoadConversationAsync(db, conversation, cancellationToken);
    }

    public async Task<ConversationSnapshot?> GetConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var conversation = await db.Conversations.SingleOrDefaultAsync(
            x => x.Id == conversationId &&
                 x.AccountId == identity.AccountId &&
                 x.Status == "Active",
            cancellationToken);
        return conversation is null
            ? null
            : await LoadConversationAsync(db, conversation, cancellationToken);
    }

    public async Task<bool> ArchiveConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var conversation = await db.Conversations.SingleOrDefaultAsync(
            x => x.Id == conversationId &&
                 x.AccountId == identity.AccountId &&
                 x.Status == "Active",
            cancellationToken);
        if (conversation is null)
        {
            return false;
        }

        conversation.Status = "Archived";
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RestoreConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var conversation = await db.Conversations.SingleOrDefaultAsync(
            x => x.Id == conversationId &&
                 x.AccountId == identity.AccountId &&
                 x.Status == "Archived",
            cancellationToken);
        if (conversation is null)
        {
            return false;
        }

        conversation.Status = "Active";
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RenameConversationAsync(
        Guid conversationId,
        string title,
        CancellationToken cancellationToken = default)
    {
        var normalizedTitle = ConversationTitle.Normalize(title);
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var conversation = await db.Conversations.SingleOrDefaultAsync(
            x => x.Id == conversationId &&
                 x.AccountId == identity.AccountId &&
                 (x.Status == "Active" || x.Status == "Archived"),
            cancellationToken);
        if (conversation is null)
        {
            return false;
        }

        conversation.Title = normalizedTitle;
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var conversation = await db.Conversations.SingleOrDefaultAsync(
            x => x.Id == conversationId &&
                 x.AccountId == identity.AccountId &&
                 (x.Status == "Active" || x.Status == "Archived"),
            cancellationToken);
        if (conversation is null)
        {
            return false;
        }

        conversation.Status = "Deleted";
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ConversationSnapshot> BranchConversationAsync(
        Guid conversationId,
        Guid branchMessageId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var source = await db.Conversations.SingleOrDefaultAsync(
            x => x.Id == conversationId &&
                 x.AccountId == identity.AccountId &&
                 x.Status == "Active",
            cancellationToken);
        if (source is null)
        {
            throw new InvalidOperationException("A conversa original não existe.");
        }

        var sourceMessages = await db.Messages
            .Where(x => x.ConversationId == source.Id)
            .OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Role == nameof(ChatMessageRole.User) ? 0 : 1)
            .ThenBy(x => x.Id)
            .ToArrayAsync(cancellationToken);
        var branchIndex = Array.FindIndex(
            sourceMessages,
            message => message.Id == branchMessageId);
        if (branchIndex < 0)
        {
            throw new InvalidOperationException("A mensagem de ramificação não existe.");
        }

        var now = DateTimeOffset.UtcNow;
        var branch = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            AccountId = identity.AccountId,
            ParentConversationId = source.Id,
            BranchedFromMessageId = branchMessageId,
            Title = ConversationTitle.Normalize(
                $"{source.Title ?? $"SESSÃO_{source.Id.ToString("N")[..4].ToUpperInvariant()}"} / RAMO"),
            ModelPreference = source.ModelPreference,
            SystemInstruction = source.SystemInstruction,
            Temperature = source.Temperature,
            MaximumOutputTokens = source.MaximumOutputTokens,
            Status = "Active",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Conversations.Add(branch);
        var copiedMessages = sourceMessages
            .Take(branchIndex)
            .Select(message => new MessageRecord
            {
                Id = Guid.NewGuid(),
                ConversationId = branch.Id,
                OperationId = message.OperationId,
                AiExecutionId = message.AiExecutionId,
                Role = message.Role,
                Content = message.Content,
                Status = message.Status,
                IdempotencyKey = $"branch:{branch.Id:N}:{message.Id:N}",
                CreatedAtUtc = message.CreatedAtUtc
            }).ToArray();
        db.Messages.AddRange(copiedMessages);
        var copiedBySourceId = sourceMessages.Take(branchIndex)
            .Zip(copiedMessages)
            .ToDictionary(x => x.First.Id, x => x.Second.Id);
        var sourceMessageIds = copiedBySourceId.Keys.ToArray();
        var sourceAttachments = await db.ChatAttachments
            .Where(x => x.MessageId.HasValue && sourceMessageIds.Contains(x.MessageId.Value) && x.Status == "Attached")
            .ToArrayAsync(cancellationToken);
        db.ChatAttachments.AddRange(sourceAttachments.Select(attachment => new ChatAttachmentRecord
        {
            Id = Guid.NewGuid(),
            AccountId = identity.AccountId,
            ConversationId = branch.Id,
            MessageId = copiedBySourceId[attachment.MessageId!.Value],
            StorageKey = attachment.StorageKey,
            FileName = attachment.FileName,
            ContentType = attachment.ContentType,
            SizeBytes = attachment.SizeBytes,
            Sha256 = attachment.Sha256,
            Status = "Attached",
            CreatedAtUtc = attachment.CreatedAtUtc
        }));
        var sourceFeedback = await db.MessageFeedbacks
            .Where(x => sourceMessageIds.Contains(x.MessageId))
            .ToArrayAsync(cancellationToken);
        db.MessageFeedbacks.AddRange(sourceFeedback.Select(feedback => new MessageFeedbackRecord
        {
            Id = Guid.NewGuid(),
            AccountId = identity.AccountId,
            MessageId = copiedBySourceId[feedback.MessageId],
            Rating = feedback.Rating,
            Reason = feedback.Reason,
            CreatedAtUtc = feedback.CreatedAtUtc,
            UpdatedAtUtc = feedback.UpdatedAtUtc
        }));
        await db.SaveChangesAsync(cancellationToken);
        return await LoadConversationAsync(db, branch, cancellationToken);
    }

    public async Task<bool> UpdateConversationAiSettingsAsync(
        Guid conversationId,
        ConversationAiSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = settings.Normalize();
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var conversation = await db.Conversations.SingleOrDefaultAsync(
            x => x.Id == conversationId &&
                 x.AccountId == identity.AccountId &&
                 x.Status == "Active",
            cancellationToken);
        if (conversation is null)
        {
            return false;
        }

        ApplySettings(conversation, normalized);
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ChatPresetSnapshot>> ListChatPresetsAsync(
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ChatPresets
            .Where(x => x.AccountId == identity.AccountId)
            .OrderBy(x => x.Name)
            .Select(x => new ChatPresetSnapshot(
                x.Id,
                x.Name,
                new ConversationAiSettings(
                    x.ModelPreference,
                    x.SystemInstruction,
                    x.Temperature,
                    x.MaximumOutputTokens),
                x.CreatedAtUtc,
                x.UpdatedAtUtc))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<ChatPresetSnapshot> SaveChatPresetAsync(
        string name,
        ConversationAiSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizePresetName(name);
        var normalizedKey = normalizedName.ToUpperInvariant();
        var normalizedSettings = settings.Normalize();
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await db.ChatPresets.SingleOrDefaultAsync(
            x => x.AccountId == identity.AccountId && x.NormalizedName == normalizedKey,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (record is null)
        {
            record = new ChatPresetRecord
            {
                Id = Guid.NewGuid(),
                AccountId = identity.AccountId,
                CreatedAtUtc = now
            };
            db.ChatPresets.Add(record);
        }

        record.Name = normalizedName;
        record.NormalizedName = normalizedKey;
        record.ModelPreference = normalizedSettings.ModelPreference;
        record.SystemInstruction = normalizedSettings.SystemInstruction;
        record.Temperature = normalizedSettings.Temperature;
        record.MaximumOutputTokens = normalizedSettings.MaximumOutputTokens;
        record.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return new ChatPresetSnapshot(
            record.Id,
            record.Name,
            normalizedSettings,
            record.CreatedAtUtc,
            record.UpdatedAtUtc);
    }

    public async Task<bool> DeleteChatPresetAsync(
        Guid presetId,
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var preset = await db.ChatPresets.SingleOrDefaultAsync(
            x => x.Id == presetId && x.AccountId == identity.AccountId,
            cancellationToken);
        if (preset is null)
        {
            return false;
        }

        db.ChatPresets.Remove(preset);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetMessageFeedbackAsync(
        Guid messageId,
        ChatFeedbackRating? rating,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedReason = NormalizeFeedbackReason(reason);
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var authorized = await (
            from message in db.Messages
            join conversation in db.Conversations on message.ConversationId equals conversation.Id
            where message.Id == messageId &&
                  message.Role == nameof(ChatMessageRole.Assistant) &&
                  conversation.AccountId == identity.AccountId &&
                  conversation.Status != "Deleted"
            select message.Id).AnyAsync(cancellationToken);
        if (!authorized) return false;

        var existing = await db.MessageFeedbacks.SingleOrDefaultAsync(
            x => x.MessageId == messageId && x.AccountId == identity.AccountId,
            cancellationToken);
        if (rating is null)
        {
            if (existing is not null)
            {
                db.MessageFeedbacks.Remove(existing);
                await db.SaveChangesAsync(cancellationToken);
            }
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            existing = new MessageFeedbackRecord
            {
                Id = Guid.NewGuid(),
                AccountId = identity.AccountId,
                MessageId = messageId,
                CreatedAtUtc = now
            };
            db.MessageFeedbacks.Add(existing);
        }
        existing.Rating = rating.Value.ToString();
        existing.Reason = rating == ChatFeedbackRating.Negative ? normalizedReason : null;
        existing.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ConversationSnapshot> GetOrCreateCurrentConversationAsync(
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var conversation = await db.Conversations
            .Where(x => x.AccountId == identity.AccountId && x.Status == "Active")
            .OrderByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (conversation is null)
        {
            var now = DateTimeOffset.UtcNow;
            conversation = new ConversationRecord
            {
                Id = Guid.NewGuid(),
                AccountId = identity.AccountId,
                Status = "Active",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync(cancellationToken);
        }

        return await LoadConversationAsync(db, conversation, cancellationToken);
    }

    public async Task<ChatExecutionResult?> FindAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var stored = await (
            from request in db.AiRequests
            join execution in db.AiExecutions on request.Id equals execution.AiRequestId
            where request.AccountId == identity.AccountId &&
                  request.IdempotencyKey == idempotencyKey &&
                  execution.Status == "Completed"
            select new { request, execution }).SingleOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return null;
        }

        return new ChatExecutionResult(
            stored.request.ConversationId,
            stored.request.OperationId,
            stored.execution.Response,
            new AiRoute(
                stored.execution.Provider,
                stored.execution.Model,
                stored.execution.PriceCatalogVersion),
            stored.execution.EstimatedCostMinor,
            stored.execution.ActualCostMinor,
            true,
            stored.execution.InputUnits,
            stored.execution.OutputUnits,
            stored.execution.StartedAtUtc,
            stored.execution.FinishedAtUtc,
            null,
            Enum.Parse<AiDataClassification>(stored.request.DataClassification));
    }

    public async Task SaveCompletedAsync(
        string prompt,
        string idempotencyKey,
        ChatExecutionResult result,
        CancellationToken cancellationToken)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.AiRequests.AnyAsync(
                x => x.AccountId == identity.AccountId &&
                     x.IdempotencyKey == idempotencyKey,
                cancellationToken))
        {
            return;
        }

        var conversation = await db.Conversations.SingleAsync(
            x => x.Id == result.ConversationId &&
                 x.AccountId == identity.AccountId,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        db.AiRequests.Add(new AiRequestRecord
        {
            Id = requestId,
            AccountId = identity.AccountId,
            ConversationId = result.ConversationId,
            OperationId = result.OperationId,
            Modality = "Chat",
            DataClassification = result.DataClassification.ToString(),
            RequestedRoute = $"{result.Route.Provider}/{result.Route.Model}",
            MaxCostMinor = result.EstimatedCostMinor,
            Status = "Completed",
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = now
        });
        db.AiExecutions.Add(new AiExecutionRecord
        {
            Id = executionId,
            AiRequestId = requestId,
            Provider = result.Route.Provider,
            Model = result.Route.Model,
            PriceCatalogVersion = result.Route.PriceCatalogVersion,
            EstimatedCostMinor = result.EstimatedCostMinor,
            ActualCostMinor = result.ActualCostMinor,
            InputUnits = result.InputUnits,
            OutputUnits = result.OutputUnits,
            Status = "Completed",
            Response = result.Response,
            StartedAtUtc = result.StartedAtUtc ?? now,
            FinishedAtUtc = result.FinishedAtUtc ?? now
        });
        var userMessageId = Guid.NewGuid();
        db.Messages.AddRange(
            new MessageRecord
            {
                Id = userMessageId,
                ConversationId = result.ConversationId,
                OperationId = result.OperationId,
                Role = nameof(ChatMessageRole.User),
                Content = prompt,
                Status = nameof(ChatMessageStatus.Completed),
                IdempotencyKey = $"{idempotencyKey}:user",
                CreatedAtUtc = now
            },
            new MessageRecord
            {
                Id = Guid.NewGuid(),
                ConversationId = result.ConversationId,
                OperationId = result.OperationId,
                AiExecutionId = executionId,
                Role = nameof(ChatMessageRole.Assistant),
                Content = result.Response,
                Status = nameof(ChatMessageStatus.Completed),
                IdempotencyKey = $"{idempotencyKey}:assistant",
                CreatedAtUtc = now.AddMilliseconds(1)
            });
        if (result.AttachmentIds is { Count: > 0 })
        {
            var ids = result.AttachmentIds.Distinct().ToArray();
            var attachments = await db.ChatAttachments
                .Where(x => ids.Contains(x.Id) && x.AccountId == identity.AccountId && x.Status == "Draft")
                .ToArrayAsync(cancellationToken);
            if (attachments.Length != ids.Length)
                throw new InvalidOperationException("Um anexo confirmado não está mais disponível.");
            foreach (var attachment in attachments)
            {
                attachment.ConversationId = result.ConversationId;
                attachment.MessageId = userMessageId;
                attachment.Status = "Attached";
            }
        }
        conversation.Title ??= ConversationTitle.FromPrompt(prompt);
        conversation.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveFailedAsync(
        string idempotencyKey,
        FailedChatExecution failure,
        CancellationToken cancellationToken)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.AiRequests.AnyAsync(
                x => x.AccountId == identity.AccountId &&
                     x.IdempotencyKey == idempotencyKey,
                cancellationToken))
        {
            return;
        }

        var conversation = await db.Conversations.SingleAsync(
            x => x.Id == failure.ConversationId &&
                 x.AccountId == identity.AccountId,
            cancellationToken);
        var requestId = Guid.NewGuid();
        db.AiRequests.Add(new AiRequestRecord
        {
            Id = requestId,
            AccountId = identity.AccountId,
            ConversationId = failure.ConversationId,
            OperationId = failure.OperationId,
            Modality = "Chat",
            DataClassification = failure.DataClassification.ToString(),
            RequestedRoute = $"{failure.Route.Provider}/{failure.Route.Model}",
            MaxCostMinor = failure.EstimatedCostMinor,
            Status = failure.FailureCode,
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = failure.StartedAtUtc
        });
        db.AiExecutions.Add(new AiExecutionRecord
        {
            Id = Guid.NewGuid(),
            AiRequestId = requestId,
            Provider = failure.Route.Provider,
            Model = failure.Route.Model,
            PriceCatalogVersion = failure.Route.PriceCatalogVersion,
            EstimatedCostMinor = failure.EstimatedCostMinor,
            ActualCostMinor = 0,
            InputUnits = 0,
            OutputUnits = 0,
            Status = failure.FailureCode,
            Response = string.Empty,
            StartedAtUtc = failure.StartedAtUtc,
            FinishedAtUtc = failure.FinishedAtUtc
        });
        conversation.UpdatedAtUtc = failure.FinishedAtUtc;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<ConversationSnapshot> LoadConversationAsync(
        AIFarmDbContext db,
        ConversationRecord conversation,
        CancellationToken cancellationToken)
    {
        var records = await db.Messages
            .Where(x => x.ConversationId == conversation.Id)
            .OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Role == nameof(ChatMessageRole.User) ? 0 : 1)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var executionIds = records
            .Where(x => x.AiExecutionId.HasValue)
            .Select(x => x.AiExecutionId!.Value)
            .Distinct()
            .ToArray();
        var executions = await db.AiExecutions
            .Where(x => executionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var messageIds = records.Select(x => x.Id).ToArray();
        var attachmentsByMessage = (await db.ChatAttachments
                .Where(x => x.MessageId.HasValue && messageIds.Contains(x.MessageId.Value) && x.Status == "Attached")
                .OrderBy(x => x.CreatedAtUtc)
                .ToArrayAsync(cancellationToken))
            .GroupBy(x => x.MessageId!.Value)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<ChatAttachmentSnapshot>)x.Select(a => new ChatAttachmentSnapshot(
                    a.Id, a.ConversationId, a.FileName, a.ContentType, a.SizeBytes,
                    a.Sha256, ChatAttachmentStatus.Attached, a.CreatedAtUtc)).ToArray());
        var feedbackByMessage = await db.MessageFeedbacks
            .Where(x => messageIds.Contains(x.MessageId))
            .ToDictionaryAsync(
                x => x.MessageId,
                x => new ChatMessageFeedback(
                    Enum.Parse<ChatFeedbackRating>(x.Rating),
                    x.Reason,
                    x.CreatedAtUtc,
                    x.UpdatedAtUtc),
                cancellationToken);

        var messages = records.Select(record =>
        {
            var execution = record.AiExecutionId is Guid executionId
                ? executions.GetValueOrDefault(executionId)
                : null;
            var route = execution is null
                ? null
                : new AiRoute(execution.Provider, execution.Model, execution.PriceCatalogVersion);
            return new ChatMessageSnapshot(
                record.Id,
                Enum.Parse<ChatMessageRole>(record.Role),
                record.Content,
                Enum.Parse<ChatMessageStatus>(record.Status),
                record.OperationId,
                record.CreatedAtUtc,
                route,
                execution?.EstimatedCostMinor,
                execution?.ActualCostMinor,
                attachmentsByMessage.GetValueOrDefault(record.Id),
                feedbackByMessage.GetValueOrDefault(record.Id));
        }).ToArray();

        return new ConversationSnapshot(
            conversation.Id,
            conversation.CreatedAtUtc,
            conversation.UpdatedAtUtc,
            messages,
            conversation.Title,
            conversation.ParentConversationId,
            conversation.BranchedFromMessageId,
            new ConversationAiSettings(
                conversation.ModelPreference,
                conversation.SystemInstruction,
                conversation.Temperature,
                conversation.MaximumOutputTokens));
    }

    private static void ApplySettings(
        ConversationRecord conversation,
        ConversationAiSettings settings)
    {
        conversation.ModelPreference = settings.ModelPreference;
        conversation.SystemInstruction = settings.SystemInstruction;
        conversation.Temperature = settings.Temperature;
        conversation.MaximumOutputTokens = settings.MaximumOutputTokens;
    }

    private static string NormalizePresetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Digite um nome para o preset.", nameof(name));
        }

        var normalized = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > 80)
        {
            throw new ArgumentException("O nome do preset deve ter no máximo 80 caracteres.", nameof(name));
        }
        return normalized;
    }

    private static string? NormalizeFeedbackReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var normalized = string.Join(' ', reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > 500)
            throw new ArgumentException("O motivo do feedback deve ter no máximo 500 caracteres.", nameof(reason));
        return normalized;
    }

    private static string EscapeLikePattern(string value) =>
        value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}

public sealed class PostgresAiUsageQuery(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor) : IAiUsageQuery
{
    public async Task<AiUsageDashboard> GetDashboardAsync(
        int recentLimit = 50,
        CancellationToken cancellationToken = default)
    {
        if (recentLimit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(recentLimit));
        }

        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var executions =
            from request in db.AiRequests
            join execution in db.AiExecutions on request.Id equals execution.AiRequestId
            join conversation in db.Conversations on request.ConversationId equals conversation.Id
            where request.AccountId == identity.AccountId
            select new
            {
                request.OperationId,
                request.ConversationId,
                ConversationTitle = conversation.Title,
                execution.Provider,
                execution.Model,
                execution.PriceCatalogVersion,
                execution.InputUnits,
                execution.OutputUnits,
                execution.EstimatedCostMinor,
                execution.ActualCostMinor,
                execution.Status,
                execution.StartedAtUtc,
                execution.FinishedAtUtc
            };

        var aggregate = await executions
            .GroupBy(_ => 1)
            .Select(group => new
            {
                ExecutionCount = group.Count(),
                ConversationCount = group.Select(x => x.ConversationId).Distinct().Count(),
                InputUnits = group.Sum(x => x.InputUnits),
                OutputUnits = group.Sum(x => x.OutputUnits),
                EstimatedCostMinor = group.Sum(x => x.EstimatedCostMinor),
                ActualCostMinor = group.Sum(x => x.ActualCostMinor)
            })
            .SingleOrDefaultAsync(cancellationToken);
        var feedbackRows = await (
            from feedback in db.MessageFeedbacks
            join message in db.Messages on feedback.MessageId equals message.Id
            where feedback.AccountId == identity.AccountId
            select new { message.OperationId, feedback.Rating, feedback.UpdatedAtUtc })
            .ToArrayAsync(cancellationToken);
        var feedbackCounts = feedbackRows
            .GroupBy(x => x.OperationId)
            .Select(x => x.OrderByDescending(item => item.UpdatedAtUtc).First().Rating)
            .GroupBy(x => x)
            .ToDictionary(x => x.Key, x => x.Count());
        var totals = aggregate is null
            ? new AiUsageTotals(
                0, 0, 0, 0, 0, 0,
                feedbackCounts.GetValueOrDefault(nameof(ChatFeedbackRating.Positive)),
                feedbackCounts.GetValueOrDefault(nameof(ChatFeedbackRating.Negative)))
            : new AiUsageTotals(
                aggregate.ExecutionCount,
                aggregate.ConversationCount,
                aggregate.InputUnits,
                aggregate.OutputUnits,
                aggregate.EstimatedCostMinor,
                aggregate.ActualCostMinor,
                feedbackCounts.GetValueOrDefault(nameof(ChatFeedbackRating.Positive)),
                feedbackCounts.GetValueOrDefault(nameof(ChatFeedbackRating.Negative)));
        var recent = await executions
            .OrderByDescending(x => x.StartedAtUtc)
            .Take(recentLimit)
            .Select(x => new AiExecutionSummary(
                x.OperationId,
                x.ConversationId,
                x.ConversationTitle,
                x.Provider,
                x.Model,
                x.PriceCatalogVersion,
                x.InputUnits,
                x.OutputUnits,
                x.EstimatedCostMinor,
                x.ActualCostMinor,
                x.Status,
                x.StartedAtUtc,
                x.FinishedAtUtc))
            .ToArrayAsync(cancellationToken);

        return new AiUsageDashboard(totals, recent);
    }
}
