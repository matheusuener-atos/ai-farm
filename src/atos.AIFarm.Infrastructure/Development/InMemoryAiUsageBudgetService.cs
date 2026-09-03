using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;

namespace atos.AIFarm.Infrastructure.Development;

public sealed class InMemoryAiUsageBudgetService(TimeProvider timeProvider) : IAiUsageBudgetService
{
    private readonly object _gate = new();
    private AiUsageBudgetPolicy _policy = AiUsageBudgetPolicy.Disabled;
    private DateTimeOffset _updatedAtUtc = timeProvider.GetUtcNow();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Dictionary<string, Guid> _idempotency = new(StringComparer.Ordinal);

    public Task<AiUsageBudgetSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(Snapshot());
    }

    public Task<AiUsageBudgetSnapshot> UpdatePolicyAsync(AiUsageBudgetPolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        policy.Validate();
        lock (_gate)
        {
            _policy = policy;
            _updatedAtUtc = timeProvider.GetUtcNow();
            return Task.FromResult(Snapshot());
        }
    }

    public Task<AiUsageBudgetReservation> ReserveAsync(
        Guid operationId, long estimatedCostMinor, string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operationId == Guid.Empty || estimatedCostMinor <= 0 || string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("A retenção do orçamento é inválida.");
        lock (_gate)
        {
            Expire();
            if (_idempotency.TryGetValue(idempotencyKey, out var existingId))
            {
                var existing = _entries[existingId];
                if (existing.OperationId != operationId || existing.ReservedMinor != estimatedCostMinor || existing.Status == "Released")
                    throw new InvalidOperationException("A chave de idempotência já foi usada por outro orçamento.");
                return Task.FromResult(new AiUsageBudgetReservation(operationId, estimatedCostMinor, _policy.IsEnabled));
            }
            var snapshot = Snapshot();
            if (_policy.IsEnabled)
            {
                if (estimatedCostMinor > _policy.MaximumPerRequestMinor)
                    throw Exceeded("por tarefa", _policy.MaximumPerRequestMinor, estimatedCostMinor);
                if (snapshot.UsedTodayMinor + snapshot.ReservedMinor + estimatedCostMinor > _policy.DailyLimitMinor)
                    throw Exceeded("diário", _policy.DailyLimitMinor, snapshot.UsedTodayMinor + snapshot.ReservedMinor + estimatedCostMinor);
                if (snapshot.UsedThisMonthMinor + snapshot.ReservedMinor + estimatedCostMinor > _policy.MonthlyLimitMinor)
                    throw Exceeded("mensal", _policy.MonthlyLimitMinor, snapshot.UsedThisMonthMinor + snapshot.ReservedMinor + estimatedCostMinor);
            }
            var entry = new Entry(operationId, idempotencyKey, estimatedCostMinor, 0, "Active",
                timeProvider.GetUtcNow(), timeProvider.GetUtcNow().AddMinutes(15));
            _entries.Add(operationId, entry);
            _idempotency.Add(idempotencyKey, operationId);
            return Task.FromResult(new AiUsageBudgetReservation(operationId, estimatedCostMinor, _policy.IsEnabled));
        }
    }

    public Task SettleAsync(Guid operationId, long actualCostMinor, CancellationToken cancellationToken = default) =>
        Finish(operationId, "Settled", actualCostMinor, cancellationToken);

    public Task ReleaseAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        Finish(operationId, "Released", 0, cancellationToken);

    private Task Finish(Guid operationId, string status, long actual, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_entries.TryGetValue(operationId, out var entry) || entry.Status == status) return Task.CompletedTask;
            if (entry.Status != "Active" || actual < 0 || actual > entry.ReservedMinor)
                throw new InvalidOperationException("A liquidação do orçamento é inválida.");
            _entries[operationId] = entry with { ActualMinor = actual, Status = status };
            return Task.CompletedTask;
        }
    }

    private AiUsageBudgetSnapshot Snapshot()
    {
        Expire();
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var day = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var usedToday = _entries.Values.Where(x => x.Status == "Settled" && x.CreatedAtUtc >= day).Sum(x => x.ActualMinor);
        var usedMonth = _entries.Values.Where(x => x.Status == "Settled" && x.CreatedAtUtc >= month).Sum(x => x.ActualMinor);
        var reserved = _entries.Values.Where(x => x.Status == "Active" && x.ExpiresAtUtc > now).Sum(x => x.ReservedMinor);
        return new AiUsageBudgetSnapshot(_policy, day, month, usedToday, usedMonth, reserved, _updatedAtUtc);
    }

    private void Expire()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var entry in _entries.Values.Where(x => x.Status == "Active" && x.ExpiresAtUtc <= now).ToArray())
            _entries[entry.OperationId] = entry with { Status = "Released" };
    }

    private static AiUsageBudgetExceededException Exceeded(string period, long limit, long projected) => new(
        $"Orçamento {period} da Iris excedido: limite {Aic.Format(limit)}, projeção {Aic.Format(projected)}.");

    private sealed record Entry(
        Guid OperationId, string IdempotencyKey, long ReservedMinor, long ActualMinor,
        string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc);
}
