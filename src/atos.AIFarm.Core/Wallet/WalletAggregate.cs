namespace atos.AIFarm.Core.Wallet;

public sealed class WalletAggregate
{
    private readonly List<LedgerTransaction> _transactions = [];
    private readonly List<BalanceReservation> _reservations = [];

    public WalletAggregate(Guid id, Guid accountId)
    {
        Id = id;
        AccountId = accountId;
    }

    public Guid Id { get; }
    public Guid AccountId { get; }
    public IReadOnlyList<LedgerTransaction> Transactions => _transactions;
    public IReadOnlyList<BalanceReservation> Reservations => _reservations;

    public WalletBalance GetBalance()
    {
        var availableByBucket = Enum.GetValues<CreditBucket>()
            .ToDictionary(bucket => bucket, bucket => BalanceFor(AccountCode.Available(Id, bucket)));
        var reserved = Enum.GetValues<CreditBucket>()
            .Sum(bucket => BalanceFor(AccountCode.Reserved(Id, bucket)));

        return new WalletBalance(availableByBucket.Values.Sum(), reserved, availableByBucket);
    }

    public LedgerTransaction GrantPromotional(
        long amountMinor,
        Guid correlationId,
        string idempotencyKey,
        DateTimeOffset now)
    {
        EnsurePositive(amountMinor);
        var existing = FindTransaction(idempotencyKey);
        if (existing is not null)
        {
            return existing;
        }

        return AddBalancedTransaction(
            LedgerTransactionType.Grant,
            correlationId,
            idempotencyKey,
            "Crédito promocional de desenvolvimento",
            now,
            Entry(AccountCode.Available(Id, CreditBucket.Promotional), CreditBucket.Promotional, LedgerDirection.Debit, amountMinor, now),
            Entry(AccountCode.PromotionalPool, CreditBucket.Promotional, LedgerDirection.Credit, amountMinor, now));
    }

    public (LedgerTransaction Transaction, bool WasReplayed) GrantPurchased(
        long amountMinor,
        Guid correlationId,
        DateTimeOffset now)
    {
        EnsurePositive(amountMinor);
        if (amountMinor > WalletLimits.MaximumAdministrativeCreditMinor)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amountMinor),
                "O crédito administrativo excede o teto por requisição.");
        }

        var idempotencyKey = PurchasedCreditKey(correlationId);
        var existing = FindTransaction(idempotencyKey);
        if (existing is not null)
        {
            return (existing, true);
        }

        var transaction = AddBalancedTransaction(
            LedgerTransactionType.Grant,
            correlationId,
            idempotencyKey,
            "Crédito administrativo no bucket Purchased",
            now,
            Entry(AccountCode.Available(Id, CreditBucket.Purchased), CreditBucket.Purchased, LedgerDirection.Debit, amountMinor, now),
            Entry(AccountCode.PurchasedAdminPool, CreditBucket.Purchased, LedgerDirection.Credit, amountMinor, now));
        return (transaction, false);
    }

    public static string PurchasedCreditKey(Guid correlationId) =>
        $"wallet:purchased:admin:{correlationId:N}";

    public BalanceReservation ReservePromotional(
        long amountMinor,
        Guid operationId,
        string idempotencyKey,
        DateTimeOffset now,
        DateTimeOffset expiresAtUtc)
    {
        EnsurePositive(amountMinor);
        var existing = _reservations.SingleOrDefault(x => x.IdempotencyKey == idempotencyKey);
        if (existing is not null)
        {
            return existing;
        }

        var available = GetBalance().AvailableByBucket[CreditBucket.Promotional];
        if (available < amountMinor)
        {
            throw new InsufficientBalanceException(available, amountMinor);
        }

        var reservation = new BalanceReservation(
            Guid.NewGuid(), Id, operationId, CreditBucket.Promotional,
            amountMinor, expiresAtUtc, idempotencyKey);

        AddBalancedTransaction(
            LedgerTransactionType.Reserve,
            operationId,
            $"{idempotencyKey}:ledger",
            "Reserva para execução de IA",
            now,
            Entry(AccountCode.Reserved(Id, reservation.Bucket), reservation.Bucket, LedgerDirection.Debit, amountMinor, now),
            Entry(AccountCode.Available(Id, reservation.Bucket), reservation.Bucket, LedgerDirection.Credit, amountMinor, now));
        _reservations.Add(reservation);
        return reservation;
    }

    public LedgerTransaction Capture(
        Guid reservationId,
        long actualAmountMinor,
        string idempotencyKey,
        DateTimeOffset now)
    {
        var existing = FindTransaction(idempotencyKey);
        if (existing is not null)
        {
            return existing;
        }

        var reservation = FindActiveReservation(reservationId);
        if (reservation.ExpiresAtUtc <= now)
        {
            throw new InvalidOperationException("A reserva expirou e não pode ser capturada.");
        }

        if (actualAmountMinor <= 0 || actualAmountMinor > reservation.ReservedAmountMinor)
        {
            throw new ArgumentOutOfRangeException(nameof(actualAmountMinor));
        }

        var capture = AddBalancedTransaction(
            LedgerTransactionType.Capture,
            reservation.OperationId,
            idempotencyKey,
            "Liquidação do custo real de IA",
            now,
            Entry(AccountCode.AiConsumption, reservation.Bucket, LedgerDirection.Debit, actualAmountMinor, now),
            Entry(AccountCode.Reserved(Id, reservation.Bucket), reservation.Bucket, LedgerDirection.Credit, actualAmountMinor, now));

        var remainder = reservation.ReservedAmountMinor - actualAmountMinor;
        if (remainder > 0)
        {
            AddBalancedTransaction(
                LedgerTransactionType.Release,
                reservation.OperationId,
                $"{idempotencyKey}:remainder",
                "Liberação da diferença entre estimativa e custo real",
                now,
                Entry(AccountCode.Available(Id, reservation.Bucket), reservation.Bucket, LedgerDirection.Debit, remainder, now),
                Entry(AccountCode.Reserved(Id, reservation.Bucket), reservation.Bucket, LedgerDirection.Credit, remainder, now));
        }

        reservation.CapturedAmountMinor = actualAmountMinor;
        reservation.Status = ReservationStatus.Captured;
        return capture;
    }

    public LedgerTransaction Release(
        Guid reservationId,
        string idempotencyKey,
        string reason,
        DateTimeOffset now)
    {
        var existing = FindTransaction(idempotencyKey);
        if (existing is not null)
        {
            return existing;
        }

        var reservation = _reservations.Single(x => x.Id == reservationId);
        if (reservation.Status is ReservationStatus.Released or ReservationStatus.Expired)
        {
            return _transactions.Last(x =>
                x.Type == LedgerTransactionType.Release &&
                x.CorrelationId == reservation.OperationId);
        }

        if (reservation.Status != ReservationStatus.Active)
        {
            throw new InvalidOperationException("Somente uma reserva ativa pode ser liberada.");
        }

        var release = AddBalancedTransaction(
            LedgerTransactionType.Release,
            reservation.OperationId,
            idempotencyKey,
            reason,
            now,
            Entry(AccountCode.Available(Id, reservation.Bucket), reservation.Bucket, LedgerDirection.Debit, reservation.ReservedAmountMinor, now),
            Entry(AccountCode.Reserved(Id, reservation.Bucket), reservation.Bucket, LedgerDirection.Credit, reservation.ReservedAmountMinor, now));
        reservation.Status = ReservationStatus.Released;
        return release;
    }

    public LedgerTransaction Expire(
        Guid reservationId,
        string idempotencyKey,
        DateTimeOffset now)
    {
        var existing = FindTransaction(idempotencyKey);
        if (existing is not null)
        {
            return existing;
        }

        var reservation = FindActiveReservation(reservationId);
        if (reservation.ExpiresAtUtc > now)
        {
            throw new InvalidOperationException("A reserva ainda não expirou.");
        }

        var release = AddBalancedTransaction(
            LedgerTransactionType.Release,
            reservation.OperationId,
            idempotencyKey,
            "Liberação automática de reserva expirada",
            now,
            Entry(AccountCode.Available(Id, reservation.Bucket), reservation.Bucket, LedgerDirection.Debit, reservation.ReservedAmountMinor, now),
            Entry(AccountCode.Reserved(Id, reservation.Bucket), reservation.Bucket, LedgerDirection.Credit, reservation.ReservedAmountMinor, now));
        reservation.Status = ReservationStatus.Expired;
        return release;
    }

    private LedgerTransaction AddBalancedTransaction(
        LedgerTransactionType type,
        Guid correlationId,
        string idempotencyKey,
        string description,
        DateTimeOffset now,
        params LedgerEntry[] entries)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("A chave de idempotência é obrigatória.", nameof(idempotencyKey));
        }

        if (entries.Sum(x => x.SignedAmountMinor) != 0)
        {
            throw new InvalidOperationException("Todo lançamento deve ser balanceado.");
        }

        var transaction = new LedgerTransaction(
            Guid.NewGuid(), Id, type, correlationId, idempotencyKey,
            description, now, entries);
        _transactions.Add(transaction);
        return transaction;
    }

    private long BalanceFor(string accountCode) => _transactions
        .SelectMany(x => x.Entries)
        .Where(x => x.AccountCode == accountCode)
        .Sum(x => x.SignedAmountMinor);

    private LedgerTransaction? FindTransaction(string idempotencyKey) =>
        _transactions.SingleOrDefault(x => x.IdempotencyKey == idempotencyKey);

    private BalanceReservation FindActiveReservation(Guid reservationId)
    {
        var reservation = _reservations.Single(x => x.Id == reservationId);
        if (reservation.Status != ReservationStatus.Active)
        {
            throw new InvalidOperationException("A reserva não está ativa.");
        }

        return reservation;
    }

    private static LedgerEntry Entry(
        string accountCode,
        CreditBucket bucket,
        LedgerDirection direction,
        long amountMinor,
        DateTimeOffset now) =>
        new(Guid.NewGuid(), accountCode, bucket, direction, amountMinor, now);

    private static void EnsurePositive(long amountMinor)
    {
        if (amountMinor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountMinor));
        }
    }

    private static class AccountCode
    {
        public const string PromotionalPool = "atos:PROMOTIONAL_POOL";
        public const string PurchasedAdminPool = "atos:PURCHASED_POOL:ADMIN:v1";
        public const string AiConsumption = "atos:AI_CONSUMPTION";
        public static string Available(Guid walletId, CreditBucket bucket) => $"WALLET:{walletId}:{bucket}:AVAILABLE";
        public static string Reserved(Guid walletId, CreditBucket bucket) => $"WALLET:{walletId}:{bucket}:RESERVED";
    }
}
