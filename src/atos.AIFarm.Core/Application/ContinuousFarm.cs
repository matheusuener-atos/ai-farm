namespace atos.AIFarm.Core.Application;

/// <summary>
/// Ritmo da farm contínua (ADR 0036). A verificação por reexecução é inline e
/// custa 1:1 no servidor, então o consumo é pago em servidor na mesma moeda:
/// o cooldown por node limita quantos jobs contínuos nascem por janela.
/// </summary>
public sealed record FarmContinuousOptions(
    int CooldownSeconds,
    int MaxJobsPerWindow)
{
    public static FarmContinuousOptions Default { get; } = new(20, 1);

    public FarmContinuousOptions Validate()
    {
        if (CooldownSeconds is < 5 or > 3600)
            throw new ArgumentOutOfRangeException(
                nameof(CooldownSeconds), "O cooldown deve estar entre 5 e 3600 segundos.");
        if (MaxJobsPerWindow is < 1 or > 64)
            throw new ArgumentOutOfRangeException(
                nameof(MaxJobsPerWindow), "Os jobs por janela devem estar entre 1 e 64.");
        return this;
    }
}

/// <summary>
/// Liga/desliga por conta: com a farm contínua ativa, um Agent ocioso que
/// pede trabalho e não encontra fila recebe um job de consumo na hora.
/// </summary>
public interface IContinuousFarmModeService
{
    Task<bool> GetAsync(CancellationToken cancellationToken = default);
    Task SetAsync(bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>
/// O modelo que a PLATAFORMA serve pela rede (ADR 0043): identidade central;
/// os nodes apenas hospedam o runtime local com este modelo baixado.
/// </summary>
public sealed record FarmNetworkOptions(string NetworkModel, int PremiumCandidates = 3)
{
    public static FarmNetworkOptions Default { get; } = new("llama3.2:3b");

    public FarmNetworkOptions Validate()
    {
        var model = NetworkModel?.Trim() ?? string.Empty;
        if (model.Length is < 1 or > 80)
            throw new ArgumentException("O modelo da rede deve ter entre 1 e 80 caracteres.", nameof(NetworkModel));
        // best-of-N (ADR 0045): 2-8 candidatos gerados em paralelo pela rede.
        if (PremiumCandidates is < 2 or > 8)
            throw new ArgumentOutOfRangeException(
                nameof(PremiumCandidates), "Os candidatos premium devem estar entre 2 e 8.");
        return this with { NetworkModel = model };
    }
}
