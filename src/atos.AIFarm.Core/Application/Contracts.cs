using atos.AIFarm.Core.Wallet;

namespace atos.AIFarm.Core.Application;

public sealed record WalletSnapshot(
    Guid WalletId,
    WalletBalance Balance,
    IReadOnlyList<LedgerTransaction> Transactions);

public sealed record ReservationResult(Guid ReservationId, long ReservedAmountMinor);

public interface IWalletService
{
    Task<WalletSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task GrantPromotionalAsync(long amountMinor, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<ReservationResult> ReservePromotionalAsync(Guid operationId, long amountMinor, string idempotencyKey, CancellationToken cancellationToken = default);
    Task CaptureAsync(Guid reservationId, long actualAmountMinor, string idempotencyKey, CancellationToken cancellationToken = default);
    Task ReleaseAsync(Guid reservationId, string idempotencyKey, string reason, CancellationToken cancellationToken = default);
    Task<int> ExpireReservationsAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default);
}
public sealed record AiRoute(string Provider, string Model, string PriceCatalogVersion);
public enum AiDataClassification { Standard, Trusted, Private }
public enum ChatAttachmentStatus { Draft, Attached }

public sealed record ChatAttachmentSnapshot(
    Guid Id,
    Guid ConversationId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    ChatAttachmentStatus Status,
    DateTimeOffset CreatedAtUtc);

public sealed record AiAttachmentInput(
    Guid Id,
    Guid ConversationId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    byte[] Content,
    int EstimatedInputTokens,
    bool IsImage);

public sealed record ChatAttachmentDownload(
    string FileName,
    string ContentType,
    byte[] Content);

public sealed record ChatAttachmentLimits(
    int MaximumCount,
    long MaximumFileBytes,
    long MaximumTotalBytes)
{
    public static ChatAttachmentLimits Default { get; } = new(3, 5 * 1024 * 1024, 15 * 1024 * 1024);
}
public sealed record PriceEstimate(long MaximumCostMinor);
public sealed record AiContextMessage(
    ChatMessageRole Role,
    string Content,
    bool IsSummary = false);
public sealed record ChatContextPlan(
    IReadOnlyList<AiContextMessage> Messages,
    int IncludedMessageCount,
    int OmittedMessageCount,
    int EstimatedInputTokens,
    int MaximumInputTokens)
{
    public bool UsesSummary => Messages.Any(x => x.IsSummary);
}
public sealed record ChatExecutionPlan(
    string Prompt,
    AiRoute Route,
    long MaximumCostMinor,
    ChatContextPlan? Context = null,
    ConversationAiSettings? Settings = null,
    IReadOnlyList<AiAttachmentInput>? Attachments = null,
    AiDataClassification DataClassification = AiDataClassification.Standard,
    FarmKnowledgeSearchResult? Knowledge = null);
public sealed record AiProviderRequest(
    string Prompt,
    AiRoute Route,
    IReadOnlyList<AiContextMessage>? Context = null,
    int? EstimatedInputTokens = null,
    string? SystemInstruction = null,
    decimal? Temperature = null,
    int? MaximumOutputTokens = null,
    IReadOnlyList<AiAttachmentInput>? Attachments = null,
    AiDataClassification DataClassification = AiDataClassification.Standard);
public sealed record AiProviderResult(string Content, long InputUnits, long OutputUnits, long ActualCostMinor);

public sealed record AiPriceCatalogEntry(
    AiRoute Route,
    long InputCostPerMillionMinor,
    long OutputCostPerMillionMinor,
    int MaximumOutputUnits,
    int InputEstimateOverheadUnits = 256)
{
    public long Cost(long inputUnits, long outputUnits)
    {
        if (inputUnits < 0 || outputUnits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputUnits));
        }

        return checked(
            CeilingRatio(inputUnits, InputCostPerMillionMinor) +
            CeilingRatio(outputUnits, OutputCostPerMillionMinor));
    }

    private static long CeilingRatio(long units, long costPerMillion) =>
        units == 0 ? 0 : checked((units * costPerMillion + 999_999) / 1_000_000);
}

public interface IAiPriceCatalog
{
    AiPriceCatalogEntry Get(AiRoute route);
}

public enum ChatMessageRole
{
    User,
    Assistant
}

public enum ChatMessageStatus
{
    Completed
}

public enum ChatFeedbackRating
{
    Positive,
    Negative
}

public sealed record ChatMessageFeedback(
    ChatFeedbackRating Rating,
    string? Reason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ChatMessageSnapshot(
    Guid Id,
    ChatMessageRole Role,
    string Content,
    ChatMessageStatus Status,
    Guid OperationId,
    DateTimeOffset CreatedAtUtc,
    AiRoute? Route = null,
    long? EstimatedCostMinor = null,
    long? ActualCostMinor = null,
    IReadOnlyList<ChatAttachmentSnapshot>? Attachments = null,
    ChatMessageFeedback? Feedback = null);

public sealed record ConversationSnapshot(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ChatMessageSnapshot> Messages,
    string? Title = null,
    Guid? ParentConversationId = null,
    Guid? BranchedFromMessageId = null,
    ConversationAiSettings? AiSettings = null);

public sealed record ConversationAiSettings(
    string ModelPreference,
    string? SystemInstruction,
    decimal Temperature,
    int MaximumOutputTokens)
{
    public const string AutomaticModel = "auto";
    public const int MaximumInstructionLength = 8_000;
    public const int MinimumOutputTokens = 64;
    public const int AbsoluteMaximumOutputTokens = 128_000;

    public static ConversationAiSettings Default { get; } = new(
        AutomaticModel,
        null,
        1.0m,
        512);

    public ConversationAiSettings Normalize()
    {
        var model = string.IsNullOrWhiteSpace(ModelPreference)
            ? AutomaticModel
            : ModelPreference.Trim();
        if (model.Length > 120)
        {
            throw new ArgumentException("O identificador do modelo deve ter no máximo 120 caracteres.");
        }

        var instruction = string.IsNullOrWhiteSpace(SystemInstruction)
            ? null
            : SystemInstruction.Trim();
        if (instruction?.Length > MaximumInstructionLength)
        {
            throw new ArgumentException(
                $"A instrução do sistema deve ter no máximo {MaximumInstructionLength:N0} caracteres.");
        }
        if (Temperature is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Temperature),
                "A criatividade deve ficar entre 0 e 2.");
        }
        if (MaximumOutputTokens is < MinimumOutputTokens or > AbsoluteMaximumOutputTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumOutputTokens),
                $"O limite de resposta deve ficar entre {MinimumOutputTokens:N0} e {AbsoluteMaximumOutputTokens:N0} tokens.");
        }

        return this with
        {
            ModelPreference = model,
            SystemInstruction = instruction,
            Temperature = decimal.Round(Temperature, 2)
        };
    }
}

public sealed record ChatPresetSnapshot(
    Guid Id,
    string Name,
    ConversationAiSettings Settings,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record AiModelOption(
    string Value,
    string Label,
    int MaximumOutputTokens);

public sealed record ConversationSummary(
    Guid Id,
    string? Title,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int MessageCount,
    string? Preview,
    Guid? ParentConversationId = null);

public sealed record ConversationQuery(
    string? Search = null,
    int Page = 1,
    int PageSize = 8,
    bool Archived = false)
{
    public ConversationQuery Normalize()
    {
        if (Page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Page));
        }
        if (PageSize is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(PageSize));
        }

        var search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        if (search?.Length > 120)
        {
            throw new ArgumentException(
                "A busca deve ter no máximo 120 caracteres.",
                nameof(Search));
        }

        return this with { Search = search };
    }
}

public sealed record ConversationPage(
    IReadOnlyList<ConversationSummary> Items,
    int TotalCount,
    int Page,
    int PageSize)
{
    public int TotalPages => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    public bool HasPreviousPage => Page > 1;
    public bool HasNextPage => Page < TotalPages;
}

public static class ConversationTitle
{
    public const int MaximumLength = 80;

    public static string FromPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("A mensagem não pode estar vazia.", nameof(prompt));
        }

        return NormalizeCore(prompt);
    }

    public static string Normalize(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("O título não pode estar vazio.", nameof(title));
        }

        return NormalizeCore(title);
    }

    private static string NormalizeCore(string value)
    {
        var normalized = string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaximumLength
            ? normalized
            : $"{normalized[..(MaximumLength - 1)]}…";
    }
}

public interface IPriceEstimator
{
    Task<PriceEstimate> EstimateAsync(
        string prompt,
        AiRoute route,
        CancellationToken cancellationToken);
}

public interface IAiRequestPriceEstimator
{
    Task<PriceEstimate> EstimateAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken);
}

public interface IAiRouteSelector
{
    Task<AiRoute> SelectAsync(string prompt, CancellationToken cancellationToken);

    async Task<AiRoute> SelectAsync(
        string prompt,
        string? modelPreference,
        CancellationToken cancellationToken)
    {
        var route = await SelectAsync(prompt, cancellationToken);
        if (!string.IsNullOrWhiteSpace(modelPreference) &&
            !string.Equals(modelPreference, ConversationAiSettings.AutomaticModel, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(modelPreference, route.Model, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("O modelo escolhido não está disponível neste ambiente.");
        }

        return route;
    }
}

public interface IAiModelCatalog
{
    IReadOnlyList<AiModelOption> GetOptions();
}

public interface IAiProvider
{
    Task<AiProviderResult> ExecuteAsync(AiProviderRequest request, CancellationToken cancellationToken);
}

public delegate ValueTask AiTextDeltaHandler(
    string delta,
    CancellationToken cancellationToken);

public interface IAiStreamingProvider : IAiProvider
{
    Task<AiProviderResult> ExecuteStreamingAsync(
        AiProviderRequest request,
        AiTextDeltaHandler onDelta,
        CancellationToken cancellationToken);
}

public interface IAiFailureDetails
{
    string FailureCode { get; }
    bool IsRetryable { get; }
    string UserMessage { get; }
    string? DiagnosticId { get; }
}

public sealed record ChatExecutionResult(
    Guid ConversationId,
    Guid OperationId,
    string Response,
    AiRoute Route,
    long EstimatedCostMinor,
    long ActualCostMinor,
    bool WasReplayed,
    long InputUnits = 0,
    long OutputUnits = 0,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? FinishedAtUtc = null,
    IReadOnlyList<Guid>? AttachmentIds = null,
    AiDataClassification DataClassification = AiDataClassification.Standard);

public sealed record FailedChatExecution(
    Guid ConversationId,
    Guid OperationId,
    AiRoute Route,
    long EstimatedCostMinor,
    string FailureCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    AiDataClassification DataClassification = AiDataClassification.Standard);

public sealed record ChatExecutionLimits(
    int MaximumPromptCharacters,
    long MaximumCostPerRequestMinor)
{
    public static ChatExecutionLimits Default { get; } = new(
        12_000,
        Aic.FromAic(100));
}

public sealed record AiUsageTotals(
    int ExecutionCount,
    int ConversationCount,
    long InputUnits,
    long OutputUnits,
    long EstimatedCostMinor,
    long ActualCostMinor,
    int PositiveFeedbackCount = 0,
    int NegativeFeedbackCount = 0)
{
    public long ReleasedCostMinor => EstimatedCostMinor - ActualCostMinor;
    public int FeedbackCount => PositiveFeedbackCount + NegativeFeedbackCount;
    public decimal? SatisfactionPercent => FeedbackCount == 0
        ? null
        : decimal.Round(PositiveFeedbackCount * 100m / FeedbackCount, 1);
}

public sealed record AiExecutionSummary(
    Guid OperationId,
    Guid ConversationId,
    string? ConversationTitle,
    string Provider,
    string Model,
    string PriceCatalogVersion,
    long InputUnits,
    long OutputUnits,
    long EstimatedCostMinor,
    long ActualCostMinor,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc);

public sealed record AiUsageDashboard(
    AiUsageTotals Totals,
    IReadOnlyList<AiExecutionSummary> RecentExecutions);

public interface IAiUsageQuery
{
    Task<AiUsageDashboard> GetDashboardAsync(
        int recentLimit = 50,
        CancellationToken cancellationToken = default);
    Task<AiUsageAnalytics> GetAnalyticsAsync(
        int days = 30,
        CancellationToken cancellationToken = default);
}

public interface IChatAttachmentService
{
    bool IsAvailable { get; }
    ChatAttachmentLimits Limits { get; }
    Task<ChatAttachmentSnapshot> UploadDraftAsync(
        Guid conversationId,
        string fileName,
        string contentType,
        Stream content,
        long sizeBytes,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatAttachmentSnapshot>> ListDraftsAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiAttachmentInput>> LoadDraftsAsync(
        Guid conversationId,
        IReadOnlyCollection<Guid> attachmentIds,
        CancellationToken cancellationToken = default);
    Task<bool> DeleteDraftAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default);
    Task<ChatAttachmentDownload?> DownloadAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default);
}

public interface IChatStore
{
    Task<ConversationPage> SearchConversationsAsync(
        ConversationQuery query,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversationSummary>> ListArchivedConversationsAsync(
        CancellationToken cancellationToken = default);
    Task<ConversationSnapshot> CreateConversationAsync(
        CancellationToken cancellationToken = default);
    Task<ConversationSnapshot?> GetConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
    Task<bool> ArchiveConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
    Task<bool> RestoreConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
    Task<bool> RenameConversationAsync(
        Guid conversationId,
        string title,
        CancellationToken cancellationToken = default);
    Task<bool> DeleteConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
    Task<ConversationSnapshot> BranchConversationAsync(
        Guid conversationId,
        Guid branchMessageId,
        CancellationToken cancellationToken = default);
    Task<bool> UpdateConversationAiSettingsAsync(
        Guid conversationId,
        ConversationAiSettings settings,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatPresetSnapshot>> ListChatPresetsAsync(
        CancellationToken cancellationToken = default);
    Task<ChatPresetSnapshot> SaveChatPresetAsync(
        string name,
        ConversationAiSettings settings,
        CancellationToken cancellationToken = default);
    Task<bool> DeleteChatPresetAsync(
        Guid presetId,
        CancellationToken cancellationToken = default);
    Task<bool> SetMessageFeedbackAsync(
        Guid messageId,
        ChatFeedbackRating? rating,
        string? reason = null,
        CancellationToken cancellationToken = default);
    Task<ConversationSnapshot> GetOrCreateCurrentConversationAsync(
        CancellationToken cancellationToken = default);
    Task<ChatExecutionResult?> FindAsync(string idempotencyKey, CancellationToken cancellationToken);
    Task SaveCompletedAsync(
        string prompt,
        string idempotencyKey,
        ChatExecutionResult result,
        CancellationToken cancellationToken);
    Task SaveFailedAsync(
        string idempotencyKey,
        FailedChatExecution failure,
        CancellationToken cancellationToken);
}

public enum ConversationExportFormat
{
    Markdown,
    Json
}

public sealed record ConversationExport(
    string FileName,
    string ContentType,
    byte[] Content);

public interface IConversationExportService
{
    Task<ConversationExport?> ExportAsync(
        Guid conversationId,
        ConversationExportFormat format,
        CancellationToken cancellationToken = default);
}
