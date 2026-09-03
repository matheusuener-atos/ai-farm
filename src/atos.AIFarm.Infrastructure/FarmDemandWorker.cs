using atos.AIFarm.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace atos.AIFarm.Infrastructure;

internal sealed class FarmDemandWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<FarmDemandWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IFarmDemandDispatcher>();
                var count = await dispatcher.DispatchDueAsync(stoppingToken);
                if (count > 0)
                    logger.LogInformation("{Count} demanda(s) da Farm processada(s).", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Falha ao processar a fila de demandas da Farm.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
