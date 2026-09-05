using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

/// <summary>
/// Auto-propostas (ADR 0042): quando um job verificado completa e a conta já
/// tem uma fotografia econômica salva, a última fotografia vira a política
/// pessoal — reaplicada com o CP/h medido DESTE job (nova fotografia selada
/// no job + proposta idempotente). Melhor-esforço: nunca derruba a conclusão.
/// </summary>
internal sealed class FarmRewardAutoProposer(
    IDbContextFactory<AIFarmDbContext> dbFactory,
    PostgresFarmEconomicsScenarioStore scenarios,
    PostgresFarmRewardProposalStore proposals)
{
    public async Task TryProposeAsync(
        Guid accountId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var baseline = (await scenarios.ListForAccountAsync(accountId, 1, cancellationToken))
                .FirstOrDefault();
            // Sem fotografia-base não há política pessoal: automação desligada.
            if (baseline is null || !baseline.IsIntegrityValid) return;

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var job = await db.FarmJobs.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == jobId && x.AccountId == accountId, cancellationToken);
            if (job?.DurationMilliseconds is not (> 0) || job.ComputeUnitsMilli <= 0) return;
            var computeUnitsPerHour = decimal.Round(
                job.ComputeUnitsMilli / 1_000m * 3_600_000m / job.DurationMilliseconds.Value,
                2,
                MidpointRounding.AwayFromZero);
            if (computeUnitsPerHour <= 0) return;

            var scenario = baseline.Scenario with { ComputeUnitsPerHour = computeUnitsPerHour };
            var snapshot = await scenarios.SaveForAccountAsync(
                accountId,
                $"auto {job.WorkloadVersion}",
                scenario,
                jobId,
                cancellationToken);
            await proposals.CreateForAccountAsync(
                accountId, jobId, snapshot.Id, cancellationToken);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            ArgumentException or
            ArgumentOutOfRangeException or
            DbUpdateException)
        {
            // Fotografia inválida, corrida ou economia inviável: o job segue
            // concluído e o resgate manual continua disponível.
        }
    }
}
