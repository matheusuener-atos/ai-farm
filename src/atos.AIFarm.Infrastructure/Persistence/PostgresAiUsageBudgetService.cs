using System.Data;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresAiUsageBudgetService(
    IDbContextFactory<AIFarmDbContext> dbFactory,
    IAIFarmIdentityAccessor identityAccessor,
    TimeProvider timeProvider,
    AiUsageSafetyCapPolicy safetyCap) : IAiUsageBudgetService
{
    private static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(15);

    public async Task<AiUsageBudgetSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await BuildSnapshotAsync(db, identity.AccountId, now, cancellationToken);
    }

    public async Task<AiUsageBudgetSnapshot> UpdatePolicyAsync(
        AiUsageBudgetPolicy policy,
        CancellationToken cancellationToken = default)
    {
        policy.Validate();
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await LockAccountAsync(db, identity.AccountId, cancellationToken);
        var record = await db.AiUsageBudgetPolicies.SingleOrDefaultAsync(
            x => x.AccountId == identity.AccountId, cancellationToken);
        if (record is null)
        {
            record = new AiUsageBudgetPolicyRecord { Id = Guid.NewGuid(), AccountId = identity.AccountId };
            db.AiUsageBudgetPolicies.Add(record);
        }
        record.IsEnabled = policy.IsEnabled;
        record.MaximumPerRequestMinor = policy.MaximumPerRequestMinor;
        record.DailyLimitMinor = policy.DailyLimitMinor;
        record.MonthlyLimitMinor = policy.MonthlyLimitMinor;
        record.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await BuildSnapshotAsync(db, identity.AccountId, now, cancellationToken);
    }

    public async Task<AiUsageBudgetReservation> ReserveAsync(
        Guid operationId,
        long estimatedCostMinor,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A operação é obrigatória.", nameof(operationId));
        if (estimatedCostMinor <= 0) throw new ArgumentOutOfRangeException(nameof(estimatedCostMinor));
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            throw new ArgumentException("A chave de idempotência do orçamento é inválida.", nameof(idempotencyKey));

        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await LockAccountAsync(db, identity.AccountId, cancellationToken);

        var existing = await db.AiUsageBudgetReservations.SingleOrDefaultAsync(
            x => x.AccountId == identity.AccountId && x.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.OperationId != operationId || existing.ReservedMinor != estimatedCostMinor)
                throw new InvalidOperationException("A chave de idempotência já foi usada por outro orçamento.");
            if (existing.Status == "Released")
                throw new InvalidOperationException("A retenção anterior deste orçamento já foi encerrada.");
            var existingPolicy = await GetPolicyAsync(db, identity.AccountId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AiUsageBudgetReservation(operationId, existing.ReservedMinor, existingPolicy.IsEnabled);
        }

        await db.AiUsageBudgetReservations
            .Where(x => x.AccountId == identity.AccountId && x.Status == "Active" && x.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, "Released")
                .SetProperty(x => x.SettledAtUtc, now), cancellationToken);

        var policy = await GetPolicyAsync(db, identity.AccountId, cancellationToken);
        if (policy.IsEnabled && estimatedCostMinor > policy.MaximumPerRequestMinor)
            throw Exceeded("por tarefa", policy.MaximumPerRequestMinor, estimatedCostMinor);
        if (policy.IsEnabled || safetyCap.IsEnabled)
        {
            var snapshot = await BuildSnapshotAsync(db, identity.AccountId, now, cancellationToken);
            var projectedToday = snapshot.UsedTodayMinor + snapshot.ReservedMinor + estimatedCostMinor;
            if (policy.IsEnabled)
            {
                if (projectedToday > policy.DailyLimitMinor)
                    throw Exceeded("diário", policy.DailyLimitMinor, projectedToday);
                if (snapshot.UsedThisMonthMinor + snapshot.ReservedMinor + estimatedCostMinor > policy.MonthlyLimitMinor)
                    throw Exceeded("mensal", policy.MonthlyLimitMinor, snapshot.UsedThisMonthMinor + snapshot.ReservedMinor + estimatedCostMinor);
            }
            if (safetyCap.IsEnabled && projectedToday > safetyCap.DailyCapMinor)
                throw SafetyCapReached(safetyCap.DailyCapMinor, projectedToday);
        }

        db.AiUsageBudgetReservations.Add(new AiUsageBudgetReservationRecord
        {
            Id = Guid.NewGuid(),
            AccountId = identity.AccountId,
            OperationId = operationId,
            IdempotencyKey = idempotencyKey,
            ReservedMinor = estimatedCostMinor,
            Status = "Active",
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(ReservationLifetime)
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AiUsageBudgetReservation(operationId, estimatedCostMinor, policy.IsEnabled);
    }

    public Task SettleAsync(Guid operationId, long actualCostMinor, CancellationToken cancellationToken = default) =>
        FinishAsync(operationId, "Settled", actualCostMinor, cancellationToken);

    public Task ReleaseAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        FinishAsync(operationId, "Released", 0, cancellationToken);

    private async Task FinishAsync(Guid operationId, string status, long actualCostMinor, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) return;
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.AiUsageBudgetReservations.SingleOrDefaultAsync(
            x => x.AccountId == identity.AccountId && x.OperationId == operationId,
            cancellationToken);
        if (record is null || record.Status == status) return;
        if (record.Status != "Active") throw new InvalidOperationException("A retenção do orçamento já foi encerrada.");
        if (actualCostMinor < 0 || actualCostMinor > record.ReservedMinor)
            throw new InvalidOperationException("O custo liquidado do orçamento é inválido.");
        record.Status = status;
        record.ActualMinor = actualCostMinor;
        record.SettledAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<AiUsageBudgetSnapshot> BuildSnapshotAsync(
        AIFarmDbContext db, Guid accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var day = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var policy = await GetPolicyAsync(db, accountId, cancellationToken);
        var usage =
            from request in db.AiRequests.AsNoTracking()
            join execution in db.AiExecutions.AsNoTracking() on request.Id equals execution.AiRequestId
            where request.AccountId == accountId && execution.Status == "Completed"
            select execution;
        var usedToday = await usage.Where(x => x.FinishedAtUtc >= day).SumAsync(x => (long?)x.ActualCostMinor, cancellationToken) ?? 0;
        var usedMonth = await usage.Where(x => x.FinishedAtUtc >= month).SumAsync(x => (long?)x.ActualCostMinor, cancellationToken) ?? 0;
        var reserved = await db.AiUsageBudgetReservations.AsNoTracking()
            .Where(x => x.AccountId == accountId && x.Status == "Active" && x.ExpiresAtUtc > now)
            .SumAsync(x => (long?)x.ReservedMinor, cancellationToken) ?? 0;
        var updated = await db.AiUsageBudgetPolicies.AsNoTracking()
            .Where(x => x.AccountId == accountId)
            .Select(x => (DateTimeOffset?)x.UpdatedAtUtc)
            .SingleOrDefaultAsync(cancellationToken) ?? now;
        return new AiUsageBudgetSnapshot(policy, day, month, usedToday, usedMonth, reserved, updated);
    }

    private static async Task<AiUsageBudgetPolicy> GetPolicyAsync(
        AIFarmDbContext db, Guid accountId, CancellationToken cancellationToken)
    {
        var record = await db.AiUsageBudgetPolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
        return record is null
            ? AiUsageBudgetPolicy.Disabled
            : new AiUsageBudgetPolicy(record.IsEnabled, record.MaximumPerRequestMinor, record.DailyLimitMinor, record.MonthlyLimitMinor);
    }

    private static Task LockAccountAsync(AIFarmDbContext db, Guid accountId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({accountId.ToString()}, 0))", cancellationToken);

    private static AiUsageBudgetExceededException Exceeded(string period, long limit, long projected) => new(
        $"Orçamento {period} da Iris excedido: limite {Aic.Format(limit)}, projeção {Aic.Format(projected)}.");

    private static AiUsageBudgetExceededException SafetyCapReached(long limit, long projected) => new(
        $"Teto diário de segurança do provider real atingido: limite {Aic.Format(limit)}, projeção {Aic.Format(projected)}.",
        "DailySafetyCapReached");
}
