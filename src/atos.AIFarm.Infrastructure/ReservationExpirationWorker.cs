using atos.AIFarm.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace atos.AIFarm.Infrastructure;

public sealed class ReservationExpirationWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ReservationExpirationWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var expirationService = scope.ServiceProvider
                    .GetRequiredService<IReservationExpirationService>();
                var expired = await expirationService.ExpireAsync(
                    timeProvider.GetUtcNow(),
                    stoppingToken);
                if (expired > 0)
                {
                    logger.LogInformation(
                        "{ExpiredReservationCount} expired wallet reservation(s) released.",
                        expired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Failed to release expired wallet reservations.");
            }
        }
    }
}
