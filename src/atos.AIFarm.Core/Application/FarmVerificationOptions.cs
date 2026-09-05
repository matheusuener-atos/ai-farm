namespace atos.AIFarm.Core.Application;

/// <summary>
/// Fundação do pagamento de geração de IA (ADR 0047), DESLIGADA por padrão.
///
/// Enquanto <see cref="Enabled"/> for <c>false</c> a geração continua valendo
/// CP=0 (o mesmo comportamento de hoje): sem regressão e, principalmente, sem
/// o buraco de cunhagem. O interruptor só pode ser ligado DEPOIS que as camadas
/// anti-fraude existirem — auditoria amostrada + escrow/clawback + orçamento
/// separado. Ligar antes disso permitiria imprimir CP com respostas falsas,
/// porque a demanda de geração de uma conta é roteada para os nodes da própria
/// conta (o mesmo dono controla a pergunta e a máquina).
///
/// Estes knobs ainda NÃO são consumidos por nenhum caminho de pagamento; eles
/// existem para que a fatia futura os leia de um lugar só, já versionado e
/// validado. Quando o pagamento acender, o CP de geração será
/// <c>clamp(duração_s * CpPerSecondMilli, piso, teto)</c>, concedido apenas a
/// node com sinal verificado fresco (dentro de RequiredVerifiedFreshnessSeconds)
/// e reputação suficiente.
/// </summary>
public sealed record FarmVerificationOptions(
    bool Enabled = false,
    long CpPerSecondMilli = 6_000,
    long MinGenerationCpMilli = 3_000,
    long MaxGenerationCpMilli = 100_000,
    int RequiredVerifiedFreshnessSeconds = 900)
{
    /// <summary>O padrão de produção: geração aceita, porém sem pagar CP.</summary>
    public static FarmVerificationOptions Disabled { get; } = new();

    public FarmVerificationOptions Validate()
    {
        if (CpPerSecondMilli is < 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(
                nameof(CpPerSecondMilli), "CP/s de geração deve estar entre 0 e 1.000.000 (milli).");
        if (MinGenerationCpMilli < 0)
            throw new ArgumentOutOfRangeException(
                nameof(MinGenerationCpMilli), "O piso de CP de geração não pode ser negativo.");
        if (MaxGenerationCpMilli < MinGenerationCpMilli)
            throw new ArgumentOutOfRangeException(
                nameof(MaxGenerationCpMilli), "O teto de CP de geração deve ser >= o piso.");
        if (RequiredVerifiedFreshnessSeconds is < 30 or > 86_400)
            throw new ArgumentOutOfRangeException(
                nameof(RequiredVerifiedFreshnessSeconds),
                "O frescor exigido do sinal verificado deve estar entre 30s e 86.400s.");
        return this;
    }
}
