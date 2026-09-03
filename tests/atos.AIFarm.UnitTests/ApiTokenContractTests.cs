using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class ApiTokenContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RequestNormalizesNameScopesAndExpiration()
    {
        var request = new ApiTokenCreateRequest(
            "  painel   interno  ",
            ApiTokenScope.ProfileRead | ApiTokenScope.UsageRead | ApiTokenScope.IrisExecute |
            ApiTokenScope.FarmRead | ApiTokenScope.FarmExecute,
            Now.AddDays(30));

        var normalized = request.Normalize(Now);

        Assert.Equal("painel interno", normalized.Name);
        Assert.True(normalized.Scopes.HasFlag(ApiTokenScope.UsageRead));
        Assert.True(normalized.Scopes.HasFlag(ApiTokenScope.IrisExecute));
        Assert.True(normalized.Scopes.HasFlag(ApiTokenScope.FarmRead));
        Assert.True(normalized.Scopes.HasFlag(ApiTokenScope.FarmExecute));
        Assert.Equal(TimeSpan.Zero, normalized.ExpiresAtUtc.Offset);
    }

    [Fact]
    public void RequestRejectsNoScopePastOrExcessiveExpiration()
    {
        Assert.Throws<ArgumentException>(() => new ApiTokenCreateRequest(
            "token válido", ApiTokenScope.None, Now.AddDays(1)).Normalize(Now));
        Assert.Throws<ArgumentException>(() => new ApiTokenCreateRequest(
            "token válido", ApiTokenScope.ProfileRead, Now).Normalize(Now));
        Assert.Throws<ArgumentException>(() => new ApiTokenCreateRequest(
            "token válido", ApiTokenScope.ProfileRead, Now.AddDays(367)).Normalize(Now));
        Assert.Throws<ArgumentException>(() => new ApiTokenCreateRequest(
            "token válido", (ApiTokenScope)512, Now.AddDays(1)).Normalize(Now));
    }

    [Fact]
    public void WalletCreditScopeIsAcceptedAndListedInAll()
    {
        var normalized = new ApiTokenCreateRequest(
            "crédito administrativo", ApiTokenScope.WalletCredit, Now.AddDays(1)).Normalize(Now);

        Assert.True(normalized.Scopes.HasFlag(ApiTokenScope.WalletCredit));
        Assert.True(ApiTokenScope.All.HasFlag(ApiTokenScope.WalletCredit));
    }

    [Theory]
    [InlineData("")]
    [InlineData("curta")]
    public void IrisApiRejectsInvalidIdempotencyKeys(string key)
    {
        Assert.Throws<ArgumentException>(() => IrisApiService.NormalizeIdempotencyKey(key));
    }

    [Fact]
    public void IrisApiRequestRejectsEmptyPromptAndInvalidContext()
    {
        Assert.Throws<ArgumentException>(() =>
            new IrisApiExecutionRequest("  ").Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IrisApiExecutionRequest("válido", MaximumContextMessages: 65).Validate());
    }

    [Fact]
    public void FarmApiValidatesCatalogAndSharedIdempotencyKey()
    {
        var definition = new FarmApiJobRequest("merkle-root-sha256-v1").Validate();
        Assert.Equal("MERKLE_ROOT_SHA256", definition.Workload);
        Assert.Equal("request-0001", PersonalApiIdempotencyKey.Normalize(" request-0001 "));
        Assert.Throws<ArgumentException>(() => new FarmApiJobRequest("shell-v1").Validate());
        Assert.Throws<ArgumentException>(() => PersonalApiIdempotencyKey.Normalize("curta"));
    }
}
