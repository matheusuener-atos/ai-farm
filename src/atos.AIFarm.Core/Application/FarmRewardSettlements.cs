namespace atos.AIFarm.Core.Application;

public sealed record FarmRewardPolicy(
    bool IsEnabled,
    string Version,
    long AicMinorPerThousandCp,
    long MonthlyBudgetMinor,
    long MaximumPerSettlementMinor)
{
    public static FarmRewardPolicy Disabled { get; } = new(
        false,
        "reward-policy-disabled-v1",
        0,
        0,
        0);

    public FarmRewardPolicy Validate()
    {
        var version = Version?.Trim() ?? string.Empty;
        if (version.Length is < 3 or > 80 ||
            version.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException(
                "A versão da política deve ter entre 3 e 80 caracteres ASCII alfanuméricos, '.', '-' ou '_'.",
                nameof(Version));
        }

        if (!IsEnabled)
        {
            return this with { Version = version };
        }

        if (AicMinorPerThousandCp <= 0)
            throw new ArgumentOutOfRangeException(nameof(AicMinorPerThousandCp));
        if (MonthlyBudgetMinor <= 0)
            throw new ArgumentOutOfRangeException(nameof(MonthlyBudgetMinor));
        if (MaximumPerSettlementMinor <= 0 || MaximumPerSettlementMinor > MonthlyBudgetMinor)
            throw new ArgumentOutOfRangeException(nameof(MaximumPerSettlementMinor));

        return this with { Version = version };
    }

    public long CalculateRewardMinor(long processingPointsMilli)
    {
        if (!IsEnabled)
            throw new InvalidOperationException("A política de rewards está desativada.");
        if (processingPointsMilli <= 0)
            throw new ArgumentOutOfRangeException(nameof(processingPointsMilli));

        var proportional = checked(
            (long)decimal.Floor(
                processingPointsMilli * (decimal)AicMinorPerThousandCp / 1_000_000m));
        return Math.Min(proportional, MaximumPerSettlementMinor);
    }
}

public sealed record FarmRewardSettlementSnapshot(
    Guid Id,
    Guid ProposalId,
    Guid JobId,
    Guid LedgerTransactionId,
    string PolicyVersion,
    DateTimeOffset PeriodStartUtc,
    long ProcessingPointsMilli,
    long AicMinorPerThousandCp,
    long RewardAmountMinor,
    string ProposalDigest,
    string SourceEvidenceDigest,
    DateTimeOffset SettledAtUtc,
    string Digest,
    bool IsIntegrityValid);

public sealed record FarmRewardPoolSnapshot(
    FarmRewardPolicy Policy,
    DateTimeOffset PeriodStartUtc,
    long BudgetMinor,
    long SpentMinor,
    IReadOnlyList<FarmRewardSettlementSnapshot> Settlements)
{
    public long RemainingMinor => Math.Max(0, BudgetMinor - SpentMinor);
}

public sealed record FarmRewardBatchSettlement(
    int SettledCount,
    long TotalRewardMinor,
    int RemainingCount,
    string? StoppedReason);

public interface IFarmRewardSettlementService
{
    Task<FarmRewardPoolSnapshot> GetPoolAsync(
        int settlementLimit = 20,
        CancellationToken cancellationToken = default);

    Task<FarmRewardSettlementSnapshot> SettleAsync(
        Guid proposalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resgata em sequencia todas as propostas ainda nao liquidadas da conta
    /// (ADR 0042), parando com motivo claro quando a politica ou o orcamento
    /// bloqueiam. Cada settlement individual continua atomico e idempotente.
    /// </summary>
    Task<FarmRewardBatchSettlement> SettleAllAsync(
        int maximumProposals = 200,
        CancellationToken cancellationToken = default);
}

public sealed class FarmRewardPoolUnavailableException(string message)
    : InvalidOperationException(message);
