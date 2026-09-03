using atos.AIFarm.Core.Application;

namespace atos.AIFarm.Infrastructure.Development;

public sealed class InMemoryInterfaceModeService : IInterfaceModeService
{
    private AIFarmInterfaceMode _mode = AIFarmInterfaceMode.Pro;

    public Task<AIFarmInterfaceMode> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_mode);

    public Task SetAsync(AIFarmInterfaceMode mode, CancellationToken cancellationToken = default)
    {
        _mode = mode;
        return Task.CompletedTask;
    }
}
