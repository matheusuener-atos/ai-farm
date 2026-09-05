using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Core.Wallet;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresFarmRewardSettlementService : IFarmRewardSettlementService
{
    public const string CurrentVersion = "atos-farm-reward-settlement-v1";

    private readonly IDbContextFactory<AIFarmDbContext> _dbFactory;
    private readonly IAIFarmIdentityAccessor _identityAccessor;
    private readonly IWalletService _walletService;
    private readonly FarmManifestSigningOptions _signingOptions;
    private readonly TimeProvider _timeProvider;
    private readonly FarmRewardPolicy _policy;

    public PostgresFarmRewardSettlementService(
        IDbContextFactory<AIFarmDbContext> dbFactory,
        IAIFarmIdentityAccessor identityAccessor,
        IWalletService walletService,
        FarmManifestSigningOptions signingOptions,
        TimeProvider timeProvider,
        FarmRewardPolicy policy)
    {
        _dbFactory = dbFactory;
        _identityAccessor = identityAccessor;
        _walletService = walletService;
        _signingOptions = signingOptions;
        _timeProvider = timeProvider;
        _policy = policy.Validate();
    }

    public async Task<FarmRewardPoolSnapshot> GetPoolAsync(
        int settlementLimit = 20,
        CancellationToken cancellationToken = default)
    {
        if (settlementLimit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(settlementLimit));

        var identity = await _identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var periodStart = PeriodStart(_timeProvider.GetUtcNow());
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var period = await db.FarmRewardBudgetPeriods.AsNoTracking().SingleOrDefaultAsync(
            x => x.PolicyVersion == _policy.Version && x.PeriodStartUtc == periodStart,
            cancellationToken);
        var records = await db.FarmRewardSettlements.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId)
            .OrderByDescending(x => x.SettledAtUtc)
            .Take(settlementLimit)
            .ToArrayAsync(cancellationToken);

        return new FarmRewardPoolSnapshot(
            _policy,
            periodStart,
            period?.BudgetMinor ?? (_policy.IsEnabled ? _policy.MonthlyBudgetMinor : 0),
            period?.SpentMinor ?? 0,
            records.Select(ToSnapshot).ToArray());
    }

    public async Task<FarmRewardBatchSettlement> SettleAllAsync(
        int maximumProposals = 200,
        CancellationToken cancellationToken = default)
    {
        var identity = await _identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var bounded = Math.Clamp(maximumProposals, 1, 500);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var pending = await db.FarmRewardProposals.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId &&
                        !db.FarmRewardSettlements.Any(settled => settled.ProposalId == x.Id))
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => x.Id)
            .Take(bounded + 1)
            .ToArrayAsync(cancellationToken);
        var queue = pending.Take(bounded).ToArray();
        var settledCount = 0;
        long totalRewardMinor = 0;
        string? stoppedReason = null;
        foreach (var proposalId in queue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var settlement = await SettleAsync(proposalId, cancellationToken);
                settledCount++;
                totalRewardMinor += settlement.RewardAmountMinor;
            }
            catch (FarmRewardPoolUnavailableException exception)
            {
                // Politica desligada ou orcamento do periodo esgotado: parar
                // aqui preserva a ordem e deixa o motivo explicito.
                stoppedReason = exception.Message;
                break;
            }
            catch (InvalidOperationException)
            {
                // Proposta individual invalida (integridade divergiu): pula e
                // segue - o resgate manual dela continua possivel para exame.
            }
        }
        var remaining = pending.Length - settledCount;
        return new FarmRewardBatchSettlement(
            settledCount, totalRewardMinor, Math.Max(0, remaining), stoppedReason);
    }

    public async Task<FarmRewardSettlementSnapshot> SettleAsync(
        Guid proposalId,
        CancellationToken cancellationToken = default)
    {
        if (!_policy.IsEnabled)
            throw new FarmRewardPoolUnavailableException(
                "O Reward Pool está desativado. Configure uma política financiada antes de liquidar rewards.");

        var identity = await _identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        _ = await _walletService.GetSnapshotAsync(cancellationToken);
        var now = NormalizeTimestamp(_timeProvider.GetUtcNow());
        var periodStart = PeriodStart(now);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var lockKey = AdvisoryLockKey(_policy.Version, periodStart);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);
        _ = await db.Wallets.FromSqlInterpolated(
                $"SELECT * FROM aifarm.wallets WHERE \"Id\" = {identity.WalletId} FOR UPDATE")
            .SingleAsync(cancellationToken);

        var existing = await db.FarmRewardSettlements.AsNoTracking().SingleOrDefaultAsync(
            x => x.ProposalId == proposalId,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.AccountId != identity.AccountId)
                throw new InvalidOperationException("A proposta não pertence à conta.");
            await db.Database.CommitTransactionAsync(cancellationToken);
            return ToSnapshot(existing);
        }

        var proposal = await db.FarmRewardProposals.SingleOrDefaultAsync(
            x => x.Id == proposalId && x.AccountId == identity.AccountId,
            cancellationToken) ?? throw new InvalidOperationException("A proposta não pertence à conta.");
        var proposalSnapshot = PostgresFarmRewardProposalStore.ToSnapshot(proposal);
        if (!proposalSnapshot.IsIntegrityValid || proposal.RecommendedRewardBrl <= 0)
            throw new InvalidOperationException("A proposta precisa ser íntegra e economicamente positiva.");

        var job = await db.FarmJobs.AsNoTracking().SingleAsync(
            x => x.Id == proposal.JobId && x.AccountId == identity.AccountId,
            cancellationToken);
        var events = await db.FarmJobEvents.AsNoTracking()
            .Where(x => x.JobId == job.Id)
            .OrderBy(x => x.Sequence)
            .ToArrayAsync(cancellationToken);
        var evidence = FarmEvidenceSecurity.Verify(job, events, _signingOptions, out _);
        if (job.Status != FarmJobStatus.Completed.ToString() ||
            !evidence.IsValid ||
            job.EvidenceDigest != proposal.SourceEvidenceDigest ||
            job.ComputeUnitsMilli != proposal.ProcessingPointsMilli)
        {
            throw new InvalidOperationException(
                "A evidência do Job não corresponde mais à proposta de reward.");
        }

        var scenarioRecord = await db.FarmEconomicsScenarios.AsNoTracking().SingleAsync(
            x => x.Id == proposal.ScenarioId && x.AccountId == identity.AccountId,
            cancellationToken);
        var scenario = PostgresFarmEconomicsScenarioStore.ToSnapshot(scenarioRecord);
        if (!scenario.IsIntegrityValid ||
            scenario.Digest != proposal.ScenarioDigest ||
            scenario.SourceJobId != job.Id ||
            scenario.SourceEvidenceDigest != job.EvidenceDigest)
        {
            throw new InvalidOperationException(
                "O cenário econômico não corresponde mais à proposta de reward.");
        }

        var rewardAmountMinor = _policy.CalculateRewardMinor(proposal.ProcessingPointsMilli);
        if (rewardAmountMinor <= 0)
            throw new FarmRewardPoolUnavailableException(
                "O processamento desta proposta é insuficiente para gerar a menor unidade de AIC na política atual.");

        var period = await db.FarmRewardBudgetPeriods.SingleOrDefaultAsync(
            x => x.PolicyVersion == _policy.Version && x.PeriodStartUtc == periodStart,
            cancellationToken);
        if (period is null)
        {
            period = new FarmRewardBudgetPeriodRecord
            {
                Id = Guid.NewGuid(),
                PolicyVersion = _policy.Version,
                PeriodStartUtc = periodStart,
                BudgetMinor = _policy.MonthlyBudgetMinor,
                SpentMinor = 0,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.FarmRewardBudgetPeriods.Add(period);
        }
        else if (period.BudgetMinor != _policy.MonthlyBudgetMinor)
        {
            throw new FarmRewardPoolUnavailableException(
                "O orçamento configurado diverge do período já aberto. Publique uma nova versão de política.");
        }

        if (period.BudgetMinor - period.SpentMinor < rewardAmountMinor)
            throw new FarmRewardPoolUnavailableException(
                "O Reward Pool não possui orçamento suficiente neste período.");

        var transactionId = Guid.NewGuid();
        db.LedgerTransactions.Add(new LedgerTransactionRecord
        {
            Id = transactionId,
            WalletId = identity.WalletId,
            Type = LedgerTransactionType.Reward.ToString(),
            CorrelationId = proposal.Id,
            IdempotencyKey = $"farm:reward:settlement:{proposal.Id:N}:{_policy.Version}",
            Description = $"Reward da Farm pela política {_policy.Version}",
            CreatedAtUtc = now,
            SettledAtUtc = now
        });
        db.LedgerEntries.AddRange(
            Entry(
                transactionId,
                $"WALLET:{identity.WalletId}:{CreditBucket.Earned}:AVAILABLE",
                LedgerDirection.Debit,
                rewardAmountMinor,
                now),
            Entry(
                transactionId,
                $"atos:REWARD_POOL:{_policy.Version}:{periodStart:yyyy-MM}",
                LedgerDirection.Credit,
                rewardAmountMinor,
                now));

        period.SpentMinor = checked(period.SpentMinor + rewardAmountMinor);
        period.UpdatedAtUtc = now;
        var settlement = new FarmRewardSettlementRecord
        {
            Id = Guid.NewGuid(),
            AccountId = identity.AccountId,
            ProposalId = proposal.Id,
            JobId = job.Id,
            LedgerTransactionId = transactionId,
            BudgetPeriodId = period.Id,
            PolicyVersion = _policy.Version,
            PeriodStartUtc = periodStart,
            ProcessingPointsMilli = proposal.ProcessingPointsMilli,
            AicMinorPerThousandCp = _policy.AicMinorPerThousandCp,
            RewardAmountMinor = rewardAmountMinor,
            ProposalDigest = proposal.Digest,
            SourceEvidenceDigest = proposal.SourceEvidenceDigest,
            SettledAtUtc = now
        };
        settlement.Digest = ComputeDigest(settlement);
        db.FarmRewardSettlements.Add(settlement);
        await db.SaveChangesAsync(cancellationToken);
        await db.Database.CommitTransactionAsync(cancellationToken);
        return ToSnapshot(settlement);
    }

    internal static FarmRewardSettlementSnapshot ToSnapshot(FarmRewardSettlementRecord record)
    {
        var expected = ComputeDigest(record);
        var valid = record.Digest.Length == expected.Length &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(record.Digest),
                Encoding.ASCII.GetBytes(expected));
        return new FarmRewardSettlementSnapshot(
            record.Id,
            record.ProposalId,
            record.JobId,
            record.LedgerTransactionId,
            record.PolicyVersion,
            record.PeriodStartUtc,
            record.ProcessingPointsMilli,
            record.AicMinorPerThousandCp,
            record.RewardAmountMinor,
            record.ProposalDigest,
            record.SourceEvidenceDigest,
            record.SettledAtUtc,
            record.Digest,
            valid);
    }

    private static LedgerEntryRecord Entry(
        Guid transactionId,
        string accountCode,
        LedgerDirection direction,
        long amountMinor,
        DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            AccountCode = accountCode,
            Bucket = CreditBucket.Earned.ToString(),
            Direction = direction.ToString(),
            AmountMinor = amountMinor,
            CreatedAtUtc = now
        };

    private static string ComputeDigest(FarmRewardSettlementRecord record)
    {
        var material = string.Join('\n',
            CurrentVersion,
            record.Id.ToString("N"),
            record.AccountId.ToString("N"),
            record.ProposalId.ToString("N"),
            record.JobId.ToString("N"),
            record.LedgerTransactionId.ToString("N"),
            record.BudgetPeriodId.ToString("N"),
            record.PolicyVersion,
            record.PeriodStartUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            record.ProcessingPointsMilli.ToString(CultureInfo.InvariantCulture),
            record.AicMinorPerThousandCp.ToString(CultureInfo.InvariantCulture),
            record.RewardAmountMinor.ToString(CultureInfo.InvariantCulture),
            record.ProposalDigest,
            record.SourceEvidenceDigest,
            record.SettledAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static DateTimeOffset PeriodStart(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % 10));
    }

    private static long AdvisoryLockKey(string version, DateTimeOffset periodStart) =>
        BitConverter.ToInt64(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{version}:{periodStart:yyyy-MM}")),
            0);
}
