namespace atos.AIFarm.Core.Application;

/// <summary>
/// Pool de computação da plataforma (ADR 0031): demandas da conta configurada
/// podem ser despachadas para Compute Agents de outras contas que fizeram
/// opt-in explícito. Desligado por padrão — sem conta configurada, nenhuma
/// demanda cruza fronteira de conta.
/// </summary>
public sealed record FarmPlatformPoolOptions(Guid? PlatformAccountId)
{
    public static FarmPlatformPoolOptions Disabled { get; } = new((Guid?)null);

    public bool IsEnabled => PlatformAccountId is not null;

    public bool IsPlatformAccount(Guid accountId) =>
        PlatformAccountId is Guid platform && platform == accountId;
}
