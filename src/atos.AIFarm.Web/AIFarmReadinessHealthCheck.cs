using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace atos.AIFarm.Web;

public sealed class AIFarmReadinessHealthCheck(
    IAIFarmReadinessProbe readinessProbe,
    IChatAttachmentService attachmentService,
    ILogger<AIFarmReadinessHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await readinessProbe.CheckAsync(cancellationToken);
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

        if (!attachmentService.IsAvailable)
        {
            return HealthCheckResult.Degraded(
                "Chat attachments are unavailable: configure AWS_ACCESS_KEY_ID, " +
                "AWS_SECRET_ACCESS_KEY, AWS_ENDPOINT_URL_S3 and AWS_REGION.");
        }

        return HealthCheckResult.Healthy();
    }
}
