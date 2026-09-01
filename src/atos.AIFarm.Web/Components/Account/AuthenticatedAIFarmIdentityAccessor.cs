using System.Security.Claims;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Components.Authorization;

namespace atos.AIFarm.Web.Components.Account;

internal sealed class AuthenticatedAIFarmIdentityAccessor(
    AuthenticationStateProvider authenticationStateProvider,
    IHttpContextAccessor httpContextAccessor)
    : IAIFarmIdentityAccessor
{
    public async Task<AIFarmIdentity> GetRequiredIdentityAsync(
        CancellationToken cancellationToken = default)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated is not true)
        {
            var state = await authenticationStateProvider.GetAuthenticationStateAsync();
            principal = state.User;
        }

        var rawId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal.Identity?.IsAuthenticated is not true ||
            !Guid.TryParse(rawId, out var accountId))
        {
            throw new UnauthorizedAccessException("Uma conta autenticada é necessária.");
        }

        var walletId = accountId == AIFarmIdentity.Development.AccountId
            ? AIFarmIdentity.Development.WalletId
            : accountId;
        return new AIFarmIdentity(
            accountId,
            walletId,
            principal.Identity.Name ?? "Conta atos");
    }
}
