using System.Text.Encodings.Web;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace atos.AIFarm.IntegrationTests;

public sealed class ApiTokenAuthenticationHandlerTests
{
    [Fact]
    public async Task IrisBearerCreatesAccountAndScopeClaims()
    {
        var accountId = Guid.NewGuid();
        var tokenId = Guid.NewGuid();
        var authenticator = new StubAuthenticator("iris_pat_123456789abc_secret", new ApiTokenIdentity(
            tokenId, accountId, "Conta API",
            ApiTokenScope.ProfileRead | ApiTokenScope.UsageRead | ApiTokenScope.IrisExecute | ApiTokenScope.FarmRead));
        var handler = NewHandler(authenticator, "Bearer iris_pat_123456789abc_secret");

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(accountId.ToString(), result.Principal!.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal(tokenId.ToString(), result.Principal.FindFirst(AIFarmApiTokenDefaults.TokenIdClaim)?.Value);
        Assert.Contains(result.Principal.FindAll(AIFarmApiTokenDefaults.ScopeClaim), x => x.Value == "UsageRead");
        Assert.Contains(result.Principal.FindAll(AIFarmApiTokenDefaults.ScopeClaim), x => x.Value == "IrisExecute");
        Assert.Contains(result.Principal.FindAll(AIFarmApiTokenDefaults.ScopeClaim), x => x.Value == "FarmRead");
        Assert.DoesNotContain(result.Principal.FindAll(AIFarmApiTokenDefaults.ScopeClaim), x => x.Value == "AuditRead");
    }

    [Fact]
    public async Task NonIrisBearerIsIgnoredAndInvalidIrisTokenFails()
    {
        var authenticator = new StubAuthenticator("iris_pat_valid", null);
        Assert.True((await NewHandler(authenticator, "Bearer farm-agent-secret").AuthenticateAsync()).None);
        Assert.True((await NewHandler(authenticator, "Bearer iris_pat_invalid").AuthenticateAsync()).Failure is not null);
    }

    private static ApiTokenAuthenticationHandler NewHandler(IApiTokenAuthenticator authenticator, string authorization)
    {
        var handler = new ApiTokenAuthenticationHandler(
            new FixedOptionsMonitor<AuthenticationSchemeOptions>(new()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            authenticator);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = authorization;
        handler.InitializeAsync(
            new AuthenticationScheme(AIFarmApiTokenDefaults.Scheme, null, typeof(ApiTokenAuthenticationHandler)),
            context).GetAwaiter().GetResult();
        return handler;
    }

    private sealed class StubAuthenticator(string expected, ApiTokenIdentity? identity) : IApiTokenAuthenticator
    {
        public Task<ApiTokenIdentity?> AuthenticateAsync(string secret, CancellationToken cancellationToken = default) =>
            Task.FromResult(secret == expected ? identity : null);
    }

    private sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
