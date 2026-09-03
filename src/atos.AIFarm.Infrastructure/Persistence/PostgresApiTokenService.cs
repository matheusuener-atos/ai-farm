using System.Security.Cryptography;
using System.Text;
using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

public sealed class PostgresApiTokenService(
    IDbContextFactory<AIFarmDbContext> dbFactory,
    IAIFarmIdentityAccessor identityAccessor,
    TimeProvider timeProvider) : IApiTokenService, IApiTokenAuthenticator
{
    public const string TokenPrefix = "iris_pat_";

    public async Task<IReadOnlyList<ApiTokenSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ApiTokens.AsNoTracking()
            .Where(x => x.AccountId == identity.AccountId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => ToSnapshot(x))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<CreatedApiToken> CreateAsync(
        ApiTokenCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var normalized = request.Normalize(now);
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var prefix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            var secretPart = Base64Url(RandomNumberGenerator.GetBytes(32));
            var secret = $"{TokenPrefix}{prefix}_{secretPart}";
            var record = new ApiTokenRecord
            {
                Id = Guid.NewGuid(),
                AccountId = identity.AccountId,
                Name = normalized.Name,
                Prefix = prefix,
                SecretHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
                Scopes = (int)normalized.Scopes,
                CreatedAtUtc = now,
                ExpiresAtUtc = normalized.ExpiresAtUtc
            };
            db.ApiTokens.Add(record);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new CreatedApiToken(ToSnapshot(record), secret);
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                db.Entry(record).State = EntityState.Detached;
            }
        }
        throw new InvalidOperationException("Não foi possível gerar um token único.");
    }

    public async Task<bool> RevokeAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var changed = await db.ApiTokens
            .Where(x => x.Id == tokenId && x.AccountId == identity.AccountId && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAtUtc, now), cancellationToken);
        return changed == 1;
    }

    public async Task<ApiTokenIdentity?> AuthenticateAsync(
        string secret,
        CancellationToken cancellationToken = default)
    {
        if (!TryReadPrefix(secret, out var prefix)) return null;
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await (
            from token in db.ApiTokens.AsNoTracking()
            join account in db.Accounts.AsNoTracking() on token.AccountId equals account.Id
            where token.Prefix == prefix && token.RevokedAtUtc == null &&
                  token.ExpiresAtUtc > now && account.Status == "Active"
            select new { Token = token, account.DisplayName })
            .ToArrayAsync(cancellationToken);
        var match = candidates.FirstOrDefault(x =>
            x.Token.SecretHash.Length == suppliedHash.Length &&
            CryptographicOperations.FixedTimeEquals(x.Token.SecretHash, suppliedHash));
        if (match is null) return null;

        await db.ApiTokens.Where(x => x.Id == match.Token.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastUsedAtUtc, now), cancellationToken);
        return new ApiTokenIdentity(
            match.Token.Id, match.Token.AccountId, match.DisplayName,
            (ApiTokenScope)match.Token.Scopes);
    }

    private static ApiTokenSnapshot ToSnapshot(ApiTokenRecord record) => new(
        record.Id, record.Name, $"{TokenPrefix}{record.Prefix}...",
        (ApiTokenScope)record.Scopes, record.CreatedAtUtc, record.ExpiresAtUtc,
        record.LastUsedAtUtc, record.RevokedAtUtc);

    private static bool TryReadPrefix(string secret, out string prefix)
    {
        prefix = string.Empty;
        if (string.IsNullOrWhiteSpace(secret) || !secret.StartsWith(TokenPrefix, StringComparison.Ordinal) || secret.Length > 100)
            return false;
        var start = TokenPrefix.Length;
        var separator = secret.IndexOf('_', start);
        if (separator != start + 12 || separator == secret.Length - 1) return false;
        prefix = secret[start..separator];
        return prefix.All(Uri.IsHexDigit);
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
