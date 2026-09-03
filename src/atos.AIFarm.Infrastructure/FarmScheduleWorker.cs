using atos.AIFarm.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace atos.AIFarm.Infrastructure;

internal sealed class FarmScheduleWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<FarmScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IFarmScheduleDispatcher>();
                var count = await dispatcher.DispatchDueAsync(stoppingToken);
                if (count > 0)
                    logger.LogInformation("{Count} ocorrência(s) da Farm processada(s).", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Falha ao processar agendamentos da Farm.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
