namespace atos.AIFarm.Core.Application;

public enum FarmRewardProposalStatus
{
    AwaitingPolicyAndFunding
}

public sealed record FarmRewardProposalSnapshot(
    Guid Id,
    Guid JobId,
    Guid ScenarioId,
    long ProcessingPointsMilli,
    decimal RecommendedRewardBrl,
    string SourceEvidenceDigest,
    string ScenarioDigest,
    string Version,
    FarmRewardProposalStatus Status,
    DateTimeOffset CreatedAtUtc,
    string Digest,
    bool IsIntegrityValid);

public interface IFarmRewardProposalStore
{
    Task<IReadOnlyList<FarmRewardProposalSnapshot>> ListAsync(
        int limit = 20,
        CancellationToken cancellationToken = default);

    Task<FarmRewardProposalSnapshot> CreateAsync(
        Guid jobId,
        Guid scenarioId,
        CancellationToken cancellationToken = default);
}
