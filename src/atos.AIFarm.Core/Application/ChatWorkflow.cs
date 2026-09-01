using atos.AIFarm.Core.Wallet;

namespace atos.AIFarm.Core.Application;

public sealed class ChatWorkflow(
    IWalletService wallet,
    IPriceEstimator pricing,
    IAiRouteSelector router,
    IAiProvider provider,
    IChatStore chat,
    ChatExecutionLimits? executionLimits = null,
    TimeProvider? timeProvider = null)
{
    private readonly ChatExecutionLimits _executionLimits =
        ValidateLimits(executionLimits ?? ChatExecutionLimits.Default);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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
        var plan = await PrepareAsync(prompt, cancellationToken);
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
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Digite uma mensagem antes de enviar.", nameof(prompt));
        }

        var normalizedPrompt = prompt.Trim();
        if (normalizedPrompt.Length > _executionLimits.MaximumPromptCharacters)
        {
            throw new InvalidOperationException(
                $"A mensagem excede o limite de {_executionLimits.MaximumPromptCharacters:N0} caracteres.");
        }

        var route = await router.SelectAsync(normalizedPrompt, cancellationToken);
        var estimate = await pricing.EstimateAsync(
            normalizedPrompt,
            route,
            cancellationToken);
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
            estimate.MaximumCostMinor);
    }

    public async Task<ChatExecutionResult> SendAsync(
        Guid conversationId,
        ChatExecutionPlan plan,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
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
            return replay with { WasReplayed = true };
        }

        var operationId = Guid.NewGuid();
        var reservation = await wallet.ReservePromotionalAsync(
            operationId,
            plan.MaximumCostMinor,
            $"{idempotencyKey}:reserve",
            cancellationToken);

        var startedAtUtc = _timeProvider.GetUtcNow();
        try
        {
            var providerResult = await provider.ExecuteAsync(
                new AiProviderRequest(plan.Prompt, plan.Route),
                cancellationToken);
            var finishedAtUtc = _timeProvider.GetUtcNow();
            ValidateProviderResult(providerResult, plan.MaximumCostMinor);
            await wallet.CaptureAsync(
                reservation.ReservationId,
                providerResult.ActualCostMinor,
                $"{idempotencyKey}:capture",
                CancellationToken.None);

            var result = new ChatExecutionResult(
                conversationId,
                operationId,
                providerResult.Content,
                plan.Route,
                plan.MaximumCostMinor,
                providerResult.ActualCostMinor,
                false,
                providerResult.InputUnits,
                providerResult.OutputUnits,
                startedAtUtc,
                finishedAtUtc);
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
            await chat.SaveFailedAsync(
                idempotencyKey,
                new FailedChatExecution(
                    conversationId,
                    operationId,
                    plan.Route,
                    plan.MaximumCostMinor,
                    FailureCode(exception),
                    startedAtUtc,
                    _timeProvider.GetUtcNow()),
                CancellationToken.None);
            throw;
        }
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
        _ => "ProviderFailure"
    };
}
