namespace atos.AIFarm.Core.Wallet;

public static class WalletLimits
{
    /// <summary>Teto por requisição do crédito administrativo: 1.000 AIC.</summary>
    public const long MaximumAdministrativeCreditMinor = 1_000_000;
}

public static class Aic
{
    public const int MinorUnitsPerAic = 1_000;

    public static long FromAic(long amount) => checked(amount * MinorUnitsPerAic);

    public static string Format(long amountMinor) =>
        $"{amountMinor / (decimal)MinorUnitsPerAic:N3} AIC";
}
public enum CreditBucket
{
    Purchased,
    Earned,
    Promotional
}

public enum LedgerDirection
{
    Debit,
    Credit
}

public enum LedgerTransactionType
{
    Grant,
    Reserve,
    Capture,
    Release,
    Refund,
    Reward,
    Reversal
}

public enum ReservationStatus
{
    Active,
    Captured,
    Released,
    Expired
}

public sealed record LedgerEntry(
    Guid Id,
    string AccountCode,
    CreditBucket Bucket,
    LedgerDirection Direction,
    long AmountMinor,
    DateTimeOffset CreatedAtUtc)
{
    public long SignedAmountMinor =>
        Direction == LedgerDirection.Debit ? AmountMinor : -AmountMinor;
}

public sealed record LedgerTransaction(
    Guid Id,
    Guid WalletId,
    LedgerTransactionType Type,
    Guid CorrelationId,
    string IdempotencyKey,
    string Description,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<LedgerEntry> Entries);

public sealed class BalanceReservation
{
    internal BalanceReservation(
        Guid id,
        Guid walletId,
        Guid operationId,
        CreditBucket bucket,
        long reservedAmountMinor,
        DateTimeOffset expiresAtUtc,
        string idempotencyKey)
    {
        Id = id;
        WalletId = walletId;
        OperationId = operationId;
        Bucket = bucket;
        ReservedAmountMinor = reservedAmountMinor;
        ExpiresAtUtc = expiresAtUtc;
        IdempotencyKey = idempotencyKey;
    }

    public Guid Id { get; }
    public Guid WalletId { get; }
    public Guid OperationId { get; }
    public CreditBucket Bucket { get; }
    public long ReservedAmountMinor { get; }
    public long CapturedAmountMinor { get; internal set; }
    public ReservationStatus Status { get; internal set; } = ReservationStatus.Active;
    public DateTimeOffset ExpiresAtUtc { get; }
    public string IdempotencyKey { get; }
}

public sealed record WalletBalance(
    long AvailableMinor,
    long ReservedMinor,
    IReadOnlyDictionary<CreditBucket, long> AvailableByBucket);

public sealed class InsufficientBalanceException(long availableMinor, long requestedMinor)
    : InvalidOperationException(
        $"Saldo insuficiente. Disponível: {Aic.Format(availableMinor)}; solicitado: {Aic.Format(requestedMinor)}.")
{
    public long AvailableMinor { get; } = availableMinor;
    public long RequestedMinor { get; } = requestedMinor;
}
