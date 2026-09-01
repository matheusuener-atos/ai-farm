using System.Security.Claims;
using atos.AIFarm.Web.Components.Account;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;

namespace atos.AIFarm.IntegrationTests;

public sealed class AuthenticatedAIFarmIdentityAccessorTests
{
    [Fact]
    public async Task UsesAuthenticatedHttpPrincipalWithoutCallingBlazorProvider()
    {
        var accountId = Guid.NewGuid();
        var principal = CreatePrincipal(accountId, "Conta HTTP");
        var contextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        var accessor = new AuthenticatedAIFarmIdentityAccessor(
            new RejectingAuthenticationStateProvider(),
            contextAccessor);

        var identity = await accessor.GetRequiredIdentityAsync();

        Assert.Equal(accountId, identity.AccountId);
        Assert.Equal(accountId, identity.WalletId);
        Assert.Equal("Conta HTTP", identity.DisplayName);
    }

    [Fact]
    public async Task FallsBackToBlazorProviderWhenThereIsNoHttpRequest()
    {
        var accountId = Guid.NewGuid();
        var accessor = new AuthenticatedAIFarmIdentityAccessor(
            new FixedAuthenticationStateProvider(CreatePrincipal(accountId, "Conta Blazor")),
            new HttpContextAccessor());

        var identity = await accessor.GetRequiredIdentityAsync();

        Assert.Equal(accountId, identity.AccountId);
        Assert.Equal("Conta Blazor", identity.DisplayName);
    }

    private static ClaimsPrincipal CreatePrincipal(Guid accountId, string name) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, accountId.ToString()),
            new Claim(ClaimTypes.Name, name)
        ], "Test"));

    private sealed class RejectingAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            throw new InvalidOperationException("The Blazor provider must not be used by an HTTP endpoint.");
    }

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal)
        : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }
}
