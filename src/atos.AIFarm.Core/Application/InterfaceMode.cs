namespace atos.AIFarm.Core.Application;

/// <summary>
/// Modo de interface da conta (Farmer Lite / Farmer Pro). O modo muda apenas
/// navegação e linguagem das telas; nenhuma regra de negócio depende dele.
/// </summary>
public enum AIFarmInterfaceMode
{
    Lite,
    Pro
}

public interface IInterfaceModeService
{
    Task<AIFarmInterfaceMode> GetAsync(CancellationToken cancellationToken = default);
    Task SetAsync(AIFarmInterfaceMode mode, CancellationToken cancellationToken = default);
}
