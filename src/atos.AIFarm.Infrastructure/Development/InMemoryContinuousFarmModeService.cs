using atos.AIFarm.Core.Application;

namespace atos.AIFarm.Infrastructure.Development;

public sealed class InMemoryContinuousFarmModeService : IContinuousFarmModeService
{
    private volatile bool _enabled;

    public Task<bool> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_enabled);

    public Task SetAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _enabled = enabled;
        return Task.CompletedTask;
    }
}
