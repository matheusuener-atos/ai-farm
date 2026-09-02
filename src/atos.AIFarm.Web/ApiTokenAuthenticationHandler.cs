using System.Security.Claims;
using System.Text.Encodings.Web;
using atos.AIFarm.Core.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace atos.AIFarm.Web;

public static class AIFarmApiTokenDefaults
{
    public const string Scheme = "AIFarmApiToken";
    public const string ScopeClaim = "aifarm_scope";
    public const string TokenIdClaim = "aifarm_token_id";
    public const string ProfileReadPolicy = "api:profile:read";
    public const string UsageReadPolicy = "api:usage:read";
    public const string IncidentsReadPolicy = "api:incidents:read";
    public const string AuditReadPolicy = "api:audit:read";
    public const string ConversationsReadPolicy = "api:conversations:read";
    public const string IrisExecutePolicy = "api:iris:execute";
    public const string FarmReadPolicy = "api:farm:read";
}

public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiTokenAuthenticator authenticator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var secret = authorization["Bearer ".Length..].Trim();
        if (!secret.StartsWith("iris_pat_", StringComparison.Ordinal))
            return AuthenticateResult.NoResult();
        var identity = await authenticator.AuthenticateAsync(secret, Context.RequestAborted);
        if (identity is null) return AuthenticateResult.Fail("Token da Iris inválido, expirado ou revogado.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identity.AccountId.ToString()),
            new(ClaimTypes.Name, identity.AccountName),
            new(AIFarmApiTokenDefaults.TokenIdClaim, identity.TokenId.ToString())
        };
        foreach (var scope in Enum.GetValues<ApiTokenScope>())
        {
            if (scope is ApiTokenScope.None or ApiTokenScope.AllRead or ApiTokenScope.All ||
                !identity.Scopes.HasFlag(scope)) continue;
            claims.Add(new Claim(AIFarmApiTokenDefaults.ScopeClaim, scope.ToString()));
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
