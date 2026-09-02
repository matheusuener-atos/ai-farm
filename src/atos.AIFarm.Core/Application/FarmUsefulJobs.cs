using System.Text;

namespace atos.AIFarm.Core.Application;

public sealed record FarmUsefulJobRequest(
    string WorkloadId,
    string Label,
    string InputText)
{
    public FarmUsefulJobSubmission Validate()
    {
        var definition = FarmWorkloadCatalog.GetRequired(WorkloadId);
        if (definition.InputSchema is null || definition.OutputSchema is null)
            throw new ArgumentException(
                "O workload selecionado não aceita uma demanda com entrada.",
                nameof(WorkloadId));
        var label = Label?.Trim() ?? string.Empty;
        if (label.Length is < 3 or > 120 || label.Any(char.IsControl))
            throw new ArgumentException(
                "O rótulo deve conter entre 3 e 120 caracteres válidos.",
                nameof(Label));
        var digest = FarmDeterministicWorkloads.ComputeInputDigest(InputText);
        return new FarmUsefulJobSubmission(
            definition,
            label,
            definition.InputSchema,
            InputText,
            Encoding.UTF8.GetByteCount(InputText),
            digest);
    }
}

public sealed record FarmUsefulJobSubmission(
    FarmWorkloadDefinition Definition,
    string Label,
    string InputVersion,
    string InputPayload,
    int InputSizeBytes,
    string InputDigest);

public sealed record FarmUsefulJobSnapshot(
    string Label,
    string InputVersion,
    int InputSizeBytes,
    string InputDigest,
    string? OutputVersion,
    int? OutputSizeBytes,
    string? OutputDigest);

public sealed record FarmUsefulJobOutput(
    string FileName,
    byte[] Content,
    string ContentType);

public sealed record FarmRemoteJobInput(
    string Version,
    string ContentType,
    string DataClassification,
    string Payload,
    int SizeBytes,
    string Digest);

public interface IFarmUsefulJobService
{
    Task<FarmJobSnapshot> SubmitAsync(
        FarmUsefulJobRequest request,
        CancellationToken cancellationToken = default);
    Task<FarmJobSnapshot> SubmitAsync(
        FarmUsefulJobRequest request,
        Guid tokenId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FarmJobSnapshot>> ListRecentAsync(
        int count = 20,
        CancellationToken cancellationToken = default);
    Task<FarmUsefulJobOutput?> DownloadOutputAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);
}
