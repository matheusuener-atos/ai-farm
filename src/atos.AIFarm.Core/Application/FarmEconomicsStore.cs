namespace atos.AIFarm.Core.Application;

public sealed record FarmEconomicsScenarioSnapshot(
    Guid Id,
    string Name,
    string Version,
    FarmEconomicsScenario Scenario,
    FarmEconomicsResult Result,
    Guid? SourceJobId,
    string? SourceEvidenceDigest,
    DateTimeOffset CreatedAtUtc,
    string Digest,
    bool IsIntegrityValid);

public sealed record FarmEconomicsReportDownload(
    string FileName,
    byte[] Content);

public interface IFarmEconomicsScenarioStore
{
    Task<IReadOnlyList<FarmEconomicsScenarioSnapshot>> ListAsync(
        int limit = 10,
        CancellationToken cancellationToken = default);

    Task<FarmEconomicsScenarioSnapshot> SaveAsync(
        string name,
        FarmEconomicsScenario scenario,
        Guid? sourceJobId = null,
        CancellationToken cancellationToken = default);

    Task<FarmEconomicsReportDownload?> DownloadReportAsync(
        Guid scenarioId,
        CancellationToken cancellationToken = default);
}
