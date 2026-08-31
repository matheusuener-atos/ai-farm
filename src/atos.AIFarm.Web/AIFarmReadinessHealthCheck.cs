using atos.AIFarm.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace atos.AIFarm.Web;

public sealed class AIFarmReadinessHealthCheck(
    IAIFarmReadinessProbe readinessProbe,
    ILogger<AIFarmReadinessHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await readinessProbe.CheckAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Readiness check failed with {ExceptionType}: {ExceptionMessage}",
                exception.GetType().Name,
                exception.Message);
            return HealthCheckResult.Unhealthy("A required dependency is unavailable.");
        }
    }
}
