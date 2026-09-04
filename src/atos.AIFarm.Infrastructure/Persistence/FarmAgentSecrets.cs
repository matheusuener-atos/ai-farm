using System.Security.Cryptography;
using System.Text;

namespace atos.AIFarm.Infrastructure.Persistence;

/// <summary>
/// Helpers compartilhados do handshake de vínculo (ADR 0038): a chave de
/// confirmação é curta e humana porque é digitada da tela da máquina para o
/// site — a segurança vem da posse dupla, não da entropia da chave.
/// </summary>
internal static class FarmAgentSecrets
{
    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Gera a chave de confirmação no formato XXXX-XXXX (hex).</summary>
    public static string CreateConfirmationCode()
    {
        var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToUpperInvariant();
        return $"{hex[..4]}-{hex[4..]}";
    }

    /// <summary>Forma canônica para hash/comparação: maiúscula, sem hífen.</summary>
    public static string NormalizeConfirmationCode(string value) =>
        value.Trim().ToUpperInvariant().Replace("-", "");
}
