namespace atos.AIFarm.Core.Application;

[Flags]
public enum ApiTokenScope
{
    None = 0,
    ProfileRead = 1,
    UsageRead = 2,
    IncidentsRead = 4,
    AuditRead = 8,
    AllRead = ProfileRead | UsageRead | IncidentsRead | AuditRead
}

public sealed record ApiTokenCreateRequest(
    string Name,
    ApiTokenScope Scopes,
    DateTimeOffset ExpiresAtUtc)
{
    public ApiTokenCreateRequest Normalize(DateTimeOffset now)
    {
        var name = string.Join(' ', (Name ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length is < 3 or > 80)
            throw new ArgumentException("O nome do token deve ter entre 3 e 80 caracteres.", nameof(Name));
        if (Scopes == ApiTokenScope.None || (Scopes & ~ApiTokenScope.AllRead) != 0)
            throw new ArgumentException("Selecione ao menos um escopo válido.", nameof(Scopes));
        var expiration = ExpiresAtUtc.ToUniversalTime();
        if (expiration <= now)
            throw new ArgumentException("A expiração do token deve estar no futuro.", nameof(ExpiresAtUtc));
        if (expiration > now.AddDays(366))
            throw new ArgumentException("O token pode ser emitido por no máximo 366 dias.", nameof(ExpiresAtUtc));
        return this with { Name = name, ExpiresAtUtc = expiration };
    }
}

public sealed record ApiTokenSnapshot(
    Guid Id,
    string Name,
    string Prefix,
    ApiTokenScope Scopes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    DateTimeOffset? RevokedAtUtc)
{
    public bool IsRevoked => RevokedAtUtc is not null;
    public bool IsExpired(DateTimeOffset now) => ExpiresAtUtc <= now;
}

public sealed record CreatedApiToken(ApiTokenSnapshot Token, string Secret);

public sealed record ApiTokenIdentity(
    Guid TokenId,
    Guid AccountId,
    string AccountName,
    ApiTokenScope Scopes);

public interface IApiTokenService
{
    Task<IReadOnlyList<ApiTokenSnapshot>> ListAsync(CancellationToken cancellationToken = default);
    Task<CreatedApiToken> CreateAsync(ApiTokenCreateRequest request, CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(Guid tokenId, CancellationToken cancellationToken = default);
}

public interface IApiTokenAuthenticator
{
    Task<ApiTokenIdentity?> AuthenticateAsync(string secret, CancellationToken cancellationToken = default);
}
