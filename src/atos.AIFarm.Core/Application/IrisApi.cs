using atos.AIFarm.Core.Wallet;

namespace atos.AIFarm.Core.Application;

public sealed record IrisApiExecutionRequest(
    string Prompt,
    Guid? ConversationId = null,
    int? MaximumContextMessages = null,
    AiDataClassification DataClassification = AiDataClassification.Standard,
    bool UseFarmKnowledge = false,
    bool AuthorizeFarmKnowledgeExternally = false)
{
    public IrisApiExecutionRequest Validate()
    {
        if (string.IsNullOrWhiteSpace(Prompt))
            throw new ArgumentException("Digite uma mensagem antes de enviar.", nameof(Prompt));
        if (ConversationId == Guid.Empty)
            throw new ArgumentException("O identificador da conversa é inválido.", nameof(ConversationId));
        if (MaximumContextMessages is < 1 or > 64)
            throw new ArgumentOutOfRangeException(
                nameof(MaximumContextMessages),
                "O contexto deve conter entre 1 e 64 mensagens.");
        if (!Enum.IsDefined(DataClassification))
            throw new ArgumentException("A classificação de dados é inválida.", nameof(DataClassification));
        return this;
    }
}

public sealed record IrisApiExecutionResponse(
    Guid ConversationId,
    Guid OperationId,
    string Output,
    AiRoute Route,
    long EstimatedCostMinor,
    long ActualCostMinor,
    string EstimatedCost,
    string ActualCost,
    long InputUnits,
    long OutputUnits,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    bool Replayed);

public sealed class IrisApiService(IChatStore chat, ChatWorkflow workflow)
{
    public async Task<IrisApiExecutionResponse> ExecuteAsync(
        IrisApiExecutionRequest request,
        Guid tokenId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (tokenId == Guid.Empty)
            throw new ArgumentException("O identificador do token é inválido.", nameof(tokenId));
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        var storageKey = $"api:{tokenId:N}:{normalizedKey}";

        var replay = await chat.FindAsync(storageKey, cancellationToken);
        if (replay is not null)
            return ToResponse(replay with { WasReplayed = true });

        var conversation = request.ConversationId is Guid conversationId
            ? await chat.GetConversationAsync(conversationId, cancellationToken)
                ?? throw new InvalidOperationException(
                    "A conversa não existe ou não pertence à conta atual.")
            : await chat.CreateConversationAsync(cancellationToken);

        var plan = await workflow.PrepareAsync(
            conversation.Id,
            request.Prompt,
            request.MaximumContextMessages,
            dataClassification: request.DataClassification,
            useFarmKnowledge: request.UseFarmKnowledge,
            authorizeFarmKnowledgeExternally: request.AuthorizeFarmKnowledgeExternally,
            cancellationToken: cancellationToken);
        var result = await workflow.SendAsync(
            conversation.Id,
            plan,
            storageKey,
            cancellationToken);

        return ToResponse(result);
    }

    private static IrisApiExecutionResponse ToResponse(ChatExecutionResult result) =>
        new(
            result.ConversationId,
            result.OperationId,
            result.Response,
            result.Route,
            result.EstimatedCostMinor,
            result.ActualCostMinor,
            Aic.Format(result.EstimatedCostMinor),
            Aic.Format(result.ActualCostMinor),
            result.InputUnits,
            result.OutputUnits,
            result.StartedAtUtc,
            result.FinishedAtUtc,
            result.WasReplayed);

    public static string NormalizeIdempotencyKey(string value)
        => PersonalApiIdempotencyKey.Normalize(value);
}
