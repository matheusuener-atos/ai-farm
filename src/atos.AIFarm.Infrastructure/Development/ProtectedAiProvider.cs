using atos.AIFarm.Core.Application;

namespace atos.AIFarm.Infrastructure.Development;

public interface IAiProviderClient
{
    Task<AiProviderResult> ExecuteAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken);
}

public interface IAiStreamingProviderClient : IAiProviderClient
{
    Task<AiProviderResult> ExecuteStreamingAsync(
        AiProviderRequest request,
        AiTextDeltaHandler onDelta,
        CancellationToken cancellationToken);
}

public sealed record AiProviderProtectionOptions(
    TimeSpan Timeout,
    int CircuitBreakerFailureThreshold,
    TimeSpan CircuitBreakerOpenDuration)
{
    public static AiProviderProtectionOptions Default { get; } = new(
        TimeSpan.FromSeconds(30),
        3,
        TimeSpan.FromSeconds(30));
}

public sealed class AiProviderUnavailableException(string message)
    : InvalidOperationException(message);

public sealed class ProtectedAiProvider : IAiStreamingProvider
{
    private readonly IAiProviderClient _client;
    private readonly AiProviderProtectionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntilUtc;

    public ProtectedAiProvider(
        IAiProviderClient client,
        AiProviderProtectionOptions options,
        TimeProvider timeProvider)
    {
        if (options.Timeout <= TimeSpan.Zero ||
            options.CircuitBreakerFailureThreshold <= 0 ||
            options.CircuitBreakerOpenDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Timeout e parâmetros do circuit breaker devem ser positivos.");
        }

        _client = client;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<AiProviderResult> ExecuteAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken) =>
        await ExecuteProtectedAsync(
            token => _client.ExecuteAsync(request, token),
            cancellationToken);

    public async Task<AiProviderResult> ExecuteStreamingAsync(
        AiProviderRequest request,
        AiTextDeltaHandler onDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onDelta);
        if (_client is not IAiStreamingProviderClient streamingClient)
        {
            var result = await ExecuteAsync(request, cancellationToken);
            await onDelta(result.Content, cancellationToken);
            return result;
        }

        return await ExecuteProtectedAsync(
            token => streamingClient.ExecuteStreamingAsync(
                request,
                onDelta,
                token),
            cancellationToken);
    }

    private async Task<AiProviderResult> ExecuteProtectedAsync(
        Func<CancellationToken, Task<AiProviderResult>> execute,
        CancellationToken cancellationToken)
    {
        EnsureCircuitIsClosed();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            var result = await execute(timeout.Token);
            RecordSuccess();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            RecordFailure();
            throw new TimeoutException(
                $"O Provider excedeu o timeout de {_options.Timeout.TotalSeconds:N0} segundos.");
        }
        catch
        {
            RecordFailure();
            throw;
        }
    }

    private void EnsureCircuitIsClosed()
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (_openUntilUtc is null)
            {
                return;
            }
            if (_openUntilUtc <= now)
            {
                _openUntilUtc = null;
                _consecutiveFailures = 0;
                return;
            }

            throw new AiProviderUnavailableException(
                $"O circuito do Provider está aberto até {_openUntilUtc:O}.");
        }
    }

    private void RecordSuccess()
    {
        lock (_sync)
        {
            _consecutiveFailures = 0;
            _openUntilUtc = null;
        }
    }

    private void RecordFailure()
    {
        lock (_sync)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _options.CircuitBreakerFailureThreshold)
            {
                _openUntilUtc = _timeProvider.GetUtcNow().Add(_options.CircuitBreakerOpenDuration);
            }
        }
    }
}
