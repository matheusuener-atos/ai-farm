namespace A7OS.AIFarm.Core.Application;

public sealed class ChatWorkflow(
    IWalletService wallet,
    IPriceEstimator pricing,
    IAiRouteSelector router,
    IAiProvider provider,
    IChatResultStore results)
{
    public async Task<ChatExecutionResult> SendAsync(
        string prompt,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Digite uma mensagem antes de enviar.", nameof(prompt));
        }

        var replay = await results.FindAsync(idempotencyKey, cancellationToken);
        if (replay is not null)
        {
            return replay with { WasReplayed = true };
        }

        var operationId = Guid.NewGuid();
        var route = await router.SelectAsync(prompt, cancellationToken);
        var estimate = await pricing.EstimateAsync(prompt, cancellationToken);
        var reservation = await wallet.ReservePromotionalAsync(
            operationId, estimate.MaximumCostMinor, $"{idempotencyKey}:reserve", cancellationToken);

        try
        {
            var providerResult = await provider.ExecuteAsync(
                new AiProviderRequest(prompt, route), cancellationToken);
            await wallet.CaptureAsync(
                reservation.ReservationId,
                providerResult.ActualCostMinor,
                $"{idempotencyKey}:capture",
                CancellationToken.None);

            var result = new ChatExecutionResult(
                operationId,
                providerResult.Content,
                route,
                estimate.MaximumCostMinor,
                providerResult.ActualCostMinor,
                false);
            await results.SaveAsync(idempotencyKey, result, CancellationToken.None);
            return result;
        }
        catch
        {
            await wallet.ReleaseAsync(
                reservation.ReservationId,
                $"{idempotencyKey}:release",
                "Execução de IA não concluída",
                CancellationToken.None);
            throw;
        }
    }
}
