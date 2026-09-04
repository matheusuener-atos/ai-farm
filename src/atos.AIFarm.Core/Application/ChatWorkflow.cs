using atos.AIFarm.Core.Wallet;

namespace atos.AIFarm.Core.Application;

public sealed class ChatWorkflow(
    IWalletService wallet,
    IPriceEstimator pricing,
    IAiRouteSelector router,
    IAiProvider provider,
    IChatStore chat,
    ChatExecutionLimits? executionLimits = null,
    TimeProvider? timeProvider = null,
    IChatContextPlanner? contextPlanner = null,
    IAiModelCatalog? modelCatalog = null,
    IChatAttachmentService? attachmentService = null,
    IAiUsageBudgetService? usageBudget = null,
    IFarmKnowledgeSearchService? farmKnowledge = null)
{
    private readonly ChatExecutionLimits _executionLimits =
        ValidateLimits(executionLimits ?? ChatExecutionLimits.Default);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IChatContextPlanner _contextPlanner =
        contextPlanner ?? new ChatContextPlanner();

    public async Task<ChatExecutionResult> SendAsync(
        string prompt,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var conversation = await chat.GetOrCreateCurrentConversationAsync(cancellationToken);
        return await SendAsync(
            conversation.Id,
            prompt,
            idempotencyKey,
            cancellationToken);
    }

    public async Task<ChatExecutionResult> SendAsync(
        Guid conversationId,
        string prompt,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var plan = await PrepareAsync(conversationId, prompt, cancellationToken: cancellationToken);
        return await SendAsync(
            conversationId,
            plan,
            idempotencyKey,
            cancellationToken);
    }

    public async Task<ChatExecutionPlan> PrepareAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        return await PrepareCoreAsync(
            prompt,
            null,
            null,
            ConversationAiSettings.Default,
            [],
            AiDataClassification.Standard,
            false,
            false,
            cancellationToken);
    }

    public async Task<ChatExecutionPlan> PrepareAsync(
        Guid conversationId,
        string prompt,
        int? maximumContextMessages = null,
        Guid? beforeMessageId = null,
        IReadOnlyCollection<Guid>? attachmentIds = null,
        AiDataClassification dataClassification = AiDataClassification.Standard,
        CancellationToken cancellationToken = default,
        bool useFarmKnowledge = false,
        bool authorizeFarmKnowledgeExternally = false)
    {
        var conversation = await chat.GetConversationAsync(
            conversationId,
            cancellationToken) ?? throw new InvalidOperationException(
                "A conversa não existe ou não pertence à conta atual.");
        IReadOnlyList<ChatMessageSnapshot> messages = conversation.Messages;
        if (beforeMessageId is Guid messageId)
        {
            var index = conversation.Messages
                .Select((message, position) => new { message.Id, position })
                .FirstOrDefault(x => x.Id == messageId)?.position ?? -1;
            if (index < 0)
            {
                throw new InvalidOperationException(
                    "A mensagem usada como ponto de contexto não existe.");
            }
            messages = conversation.Messages.Take(index).ToArray();
        }

        IReadOnlyList<AiAttachmentInput> attachments = [];
        if (attachmentIds is { Count: > 0 })
        {
            if (attachmentService is null || !attachmentService.IsAvailable)
            {
                throw new InvalidOperationException("O armazenamento privado de anexos não está configurado neste ambiente.");
            }
            attachments = await attachmentService.LoadDraftsAsync(
                conversationId,
                attachmentIds,
                cancellationToken);
        }

        return await PrepareCoreAsync(
            prompt,
            messages,
            maximumContextMessages,
            conversation.AiSettings ?? ConversationAiSettings.Default,
            attachments,
            dataClassification,
            useFarmKnowledge,
            authorizeFarmKnowledgeExternally,
            cancellationToken);
    }

    private async Task<ChatExecutionPlan> PrepareCoreAsync(
        string prompt,
        IReadOnlyList<ChatMessageSnapshot>? messages,
        int? maximumContextMessages,
        ConversationAiSettings settings,
        IReadOnlyList<AiAttachmentInput> attachments,
        AiDataClassification dataClassification,
        bool useFarmKnowledge,
        bool authorizeFarmKnowledgeExternally,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Digite uma mensagem antes de enviar.", nameof(prompt));
        }

        var normalizedPrompt = prompt.Trim();
        if (!Enum.IsDefined(dataClassification))
        {
            throw new InvalidOperationException("A classificação de dados é inválida.");
        }
        if (normalizedPrompt.Length > _executionLimits.MaximumPromptCharacters)
        {
            throw new InvalidOperationException(
                $"A mensagem excede o limite de {_executionLimits.MaximumPromptCharacters:N0} caracteres.");
        }

        var normalizedSettings = settings.Normalize();
        if (modelCatalog is not null)
        {
            var option = modelCatalog.GetOptions().FirstOrDefault(x =>
                string.Equals(x.Value, normalizedSettings.ModelPreference, StringComparison.Ordinal) ||
                string.Equals(normalizedSettings.ModelPreference, ConversationAiSettings.AutomaticModel, StringComparison.OrdinalIgnoreCase));
            if (option is null)
            {
                throw new InvalidOperationException("O modelo escolhido não está disponível neste ambiente.");
            }
            if (normalizedSettings.MaximumOutputTokens > option.MaximumOutputTokens)
            {
                throw new InvalidOperationException(
                    $"O modelo escolhido aceita no máximo {option.MaximumOutputTokens:N0} tokens nesta instalação.");
            }
        }

        var route = await router.SelectAsync(
            normalizedPrompt,
            normalizedSettings.ModelPreference,
            cancellationToken);
        if (dataClassification == AiDataClassification.Private &&
            !string.Equals(route.Provider, "atos Fake Provider", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Dados classificados como PRIVADO não podem sair deste ambiente. Use CONFIÁVEL somente se o envio ao provedor indicado no preflight estiver autorizado.");
        }
        // Índices da Farm derivam dos dados da conta: só saem do ambiente
        // local com consentimento explícito por execução (ADR 0035). PRIVADO
        // continua bloqueando qualquer rota externa pelo gate acima.
        if (useFarmKnowledge &&
            !string.Equals(route.Provider, "atos Fake Provider", StringComparison.Ordinal) &&
            !authorizeFarmKnowledgeExternally)
        {
            throw new InvalidOperationException(
                "A Memória da Farm só sai do ambiente local com a sua autorização. Marque a autorização de envio dos índices ao provider externo da rota, ou desmarque a Memória da Farm.");
        }
        FarmKnowledgeSearchResult? knowledge = null;
        if (useFarmKnowledge && farmKnowledge is not null)
            knowledge = await farmKnowledge.SearchAsync(normalizedPrompt, cancellationToken: cancellationToken);
        var effectiveInstruction = CombineInstructions(normalizedSettings.SystemInstruction,
            knowledge is { HasHits: true } ? FarmKnowledgeRetrieval.BuildSystemContext(knowledge.Hits) : null);
        var context = _contextPlanner.Plan(
            messages ?? [],
            normalizedPrompt,
            maximumContextMessages,
            effectiveInstruction);
        var request = new AiProviderRequest(
            normalizedPrompt,
            route,
            context.Messages,
            context.EstimatedInputTokens,
            effectiveInstruction,
            normalizedSettings.Temperature,
            normalizedSettings.MaximumOutputTokens,
            attachments,
            dataClassification);
        var estimate = pricing is IAiRequestPriceEstimator requestPricing
            ? await requestPricing.EstimateAsync(request, cancellationToken)
            : await pricing.EstimateAsync(normalizedPrompt, route, cancellationToken);
        if (estimate.MaximumCostMinor <= 0)
        {
            throw new InvalidOperationException("A estimativa de custo deve ser positiva.");
        }
        if (estimate.MaximumCostMinor > _executionLimits.MaximumCostPerRequestMinor)
        {
            throw new InvalidOperationException(
                $"A estimativa excede o limite por tarefa de {Aic.Format(_executionLimits.MaximumCostPerRequestMinor)}.");
        }

        return new ChatExecutionPlan(
            normalizedPrompt,
            route,
            estimate.MaximumCostMinor,
            context,
            normalizedSettings,
            attachments,
            dataClassification,
            knowledge);
    }

    public async Task<ChatExecutionResult> SendAsync(
        Guid conversationId,
        ChatExecutionPlan plan,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        await SendCoreAsync(
            conversationId,
            plan,
            idempotencyKey,
            null,
            cancellationToken);

    public async Task<ChatExecutionResult> SendStreamingAsync(
        Guid conversationId,
        ChatExecutionPlan plan,
        string idempotencyKey,
        AiTextDeltaHandler onDelta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onDelta);
        return await SendCoreAsync(
            conversationId,
            plan,
            idempotencyKey,
            onDelta,
            cancellationToken);
    }

    private async Task<ChatExecutionResult> SendCoreAsync(
        Guid conversationId,
        ChatExecutionPlan plan,
        string idempotencyKey,
        AiTextDeltaHandler? onDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.Prompt) || plan.MaximumCostMinor <= 0)
        {
            throw new ArgumentException("O plano de execução é inválido.", nameof(plan));
        }

        if (await chat.GetConversationAsync(conversationId, cancellationToken) is null)
        {
            throw new InvalidOperationException("A conversa não existe ou não pertence à conta atual.");
        }

        var replay = await chat.FindAsync(idempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (onDelta is not null)
            {
                await onDelta(replay.Response, cancellationToken);
            }
            return replay with { WasReplayed = true };
        }

        var operationId = Guid.NewGuid();
        if (usageBudget is not null)
        {
            var budgetCheckedAtUtc = _timeProvider.GetUtcNow();
            try
            {
                await usageBudget.ReserveAsync(
                    operationId,
                    plan.MaximumCostMinor,
                    $"{idempotencyKey}:budget",
                    cancellationToken);
            }
            catch (AiUsageBudgetExceededException exception)
            {
                await chat.SaveFailedAsync(
                    idempotencyKey,
                    new FailedChatExecution(
                        conversationId,
                        operationId,
                        plan.Route,
                        plan.MaximumCostMinor,
                        FailureCode(exception),
                        budgetCheckedAtUtc,
                        _timeProvider.GetUtcNow(),
                        plan.DataClassification),
                    CancellationToken.None);
                throw;
            }
        }

        ReservationResult reservation;
        try
        {
            reservation = await wallet.ReservePromotionalAsync(
                operationId,
                plan.MaximumCostMinor,
                $"{idempotencyKey}:reserve",
                cancellationToken);
        }
        catch
        {
            if (usageBudget is not null)
            {
                try { await usageBudget.ReleaseAsync(operationId, CancellationToken.None); }
                catch { /* A retenção expira automaticamente; preserve a falha original do Ledger. */ }
            }
            throw;
        }

        var startedAtUtc = _timeProvider.GetUtcNow();
        var budgetSettled = false;
        try
        {
            var request = new AiProviderRequest(
                plan.Prompt,
                plan.Route,
                plan.Context?.Messages,
                plan.Context?.EstimatedInputTokens,
                CombineInstructions(plan.Settings?.SystemInstruction,
                    plan.Knowledge is { HasHits: true }
                        ? FarmKnowledgeRetrieval.BuildSystemContext(plan.Knowledge.Hits) : null),
                plan.Settings?.Temperature,
                plan.Settings?.MaximumOutputTokens,
                plan.Attachments,
                plan.DataClassification);
            AiProviderResult providerResult;
            if (onDelta is not null && provider is IAiStreamingProvider streamingProvider)
            {
                providerResult = await streamingProvider.ExecuteStreamingAsync(
                    request,
                    onDelta,
                    cancellationToken);
            }
            else
            {
                providerResult = await provider.ExecuteAsync(request, cancellationToken);
            }
            var finishedAtUtc = _timeProvider.GetUtcNow();
            ValidateProviderResult(providerResult, plan.MaximumCostMinor);
            var response = plan.Knowledge is { HasHits: true }
                ? FarmKnowledgeRetrieval.AppendSources(providerResult.Content, plan.Knowledge.Hits)
                : providerResult.Content;
            if (onDelta is not null)
            {
                if (provider is IAiStreamingProvider)
                {
                    if (!string.Equals(response, providerResult.Content, StringComparison.Ordinal))
                        await onDelta(response[providerResult.Content.TrimEnd().Length..], cancellationToken);
                }
                else await onDelta(response, cancellationToken);
            }
            await wallet.CaptureAsync(
                reservation.ReservationId,
                providerResult.ActualCostMinor,
                $"{idempotencyKey}:capture",
                CancellationToken.None);
            if (usageBudget is not null)
            {
                await usageBudget.SettleAsync(
                    operationId,
                    providerResult.ActualCostMinor,
                    CancellationToken.None);
                budgetSettled = true;
            }

            var result = new ChatExecutionResult(
                conversationId,
                operationId,
                response,
                plan.Route,
                plan.MaximumCostMinor,
                providerResult.ActualCostMinor,
                false,
                providerResult.InputUnits,
                providerResult.OutputUnits,
                startedAtUtc,
                finishedAtUtc,
                plan.Attachments?.Select(x => x.Id).ToArray(),
                plan.DataClassification);
            await chat.SaveCompletedAsync(
                plan.Prompt,
                idempotencyKey,
                result,
                CancellationToken.None);
            return result;
        }
        catch (Exception exception)
        {
            await wallet.ReleaseAsync(
                reservation.ReservationId,
                $"{idempotencyKey}:release",
                "Execução de IA não concluída",
                CancellationToken.None);
            if (usageBudget is not null && !budgetSettled)
                await usageBudget.ReleaseAsync(operationId, CancellationToken.None);
            await chat.SaveFailedAsync(
                idempotencyKey,
                new FailedChatExecution(
                    conversationId,
                    operationId,
                    plan.Route,
                    plan.MaximumCostMinor,
                    FailureCode(exception),
                    startedAtUtc,
                    _timeProvider.GetUtcNow(),
                    plan.DataClassification),
                CancellationToken.None);
            throw;
        }
    }

    private static string? CombineInstructions(string? configured, string? knowledge)
    {
        var first = string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
        var second = string.IsNullOrWhiteSpace(knowledge) ? null : knowledge.Trim();
        if (first is null) return second;
        if (second is null) return first;
        return $"{first}\n\n{second}";
    }

    private static void ValidateProviderResult(
        AiProviderResult result,
        long reservedAmountMinor)
    {
        if (result.InputUnits < 0 || result.OutputUnits < 0)
        {
            throw new InvalidOperationException("O Provider retornou unidades de uso inválidas.");
        }
        if (result.ActualCostMinor <= 0 || result.ActualCostMinor > reservedAmountMinor)
        {
            throw new InvalidOperationException("O custo real retornado pelo Provider é inválido.");
        }
    }

    private static ChatExecutionLimits ValidateLimits(ChatExecutionLimits limits)
    {
        if (limits.MaximumPromptCharacters <= 0 || limits.MaximumCostPerRequestMinor <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                "Os limites de execução devem ser positivos.");
        }

        return limits;
    }

    private static string FailureCode(Exception exception) => exception switch
    {
        OperationCanceledException => "Cancelled",
        TimeoutException => "Timeout",
        IAiFailureDetails details => details.FailureCode,
        _ => "ProviderFailure"
    };
}
