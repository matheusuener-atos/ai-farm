using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using atos.AIFarm.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Persistence;

internal sealed class PostgresFarmAgentService(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    TimeProvider timeProvider) : IFarmAgentService
{
    internal const int HeartbeatIntervalSeconds = 30;
    internal static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(10);

    public async Task<FarmAgentEnrollment> CreateEnrollmentAsync(
        CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        await db.FarmAgentEnrollments
            .Where(x => x.AccountId == identity.AccountId && x.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.ConsumedAtUtc, now),
                cancellationToken);

        var token = CreateSecret("afe");
        var enrollment = new FarmAgentEnrollmentRecord
        {
            Id = Guid.NewGuid(),
            AccountId = identity.AccountId,
            TokenHash = HashSecret(token),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(EnrollmentLifetime)
        };
        db.FarmAgentEnrollments.Add(enrollment);
        await db.SaveChangesAsync(cancellationToken);
        return new FarmAgentEnrollment(enrollment.Id, token, enrollment.ExpiresAtUtc);
    }

    public async Task<FarmAgentEnrollmentResult?> EnrollAsync(
        string token,
        FarmAgentEnrollmentRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = Validate(request);
        if (string.IsNullOrWhiteSpace(token)) return null;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var tokenHash = HashSecret(token);
        var consumed = await db.FarmAgentEnrollments
            .Where(x => x.TokenHash == tokenHash &&
                        x.ConsumedAtUtc == null &&
                        x.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.ConsumedAtUtc, now),
                cancellationToken);
        if (consumed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var enrollment = await db.FarmAgentEnrollments
            .SingleAsync(x => x.TokenHash == tokenHash, cancellationToken);
        var node = await db.FarmNodes
            .SingleOrDefaultAsync(x => x.AccountId == enrollment.AccountId, cancellationToken);
        if (node is null)
        {
            node = new FarmNodeRecord
            {
                Id = Guid.NewGuid(),
                AccountId = enrollment.AccountId,
                CreatedAtUtc = now
            };
            db.FarmNodes.Add(node);
        }

        var credential = CreateSecret("afc");
        node.Name = normalized.Name;
        node.Runtime = normalized.Runtime;
        node.LogicalProcessors = normalized.LogicalProcessors;
        node.MemoryBytes = normalized.MemoryBytes;
        node.AgentVersion = normalized.AgentVersion;
        node.AgentCredentialHash = HashSecret(credential);
        node.AgentCredentialIssuedAtUtc = now;
        node.ConnectionKind = FarmNodeConnectionKind.Agent.ToString();
        node.Status = FarmNodeStatus.Online.ToString();
        node.VerificationLevel = FarmVerificationLevel.Detected.ToString();
        node.LastSeenAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new FarmAgentEnrollmentResult(node.Id, credential, HeartbeatIntervalSeconds);
    }

    public async Task<FarmAgentHeartbeatResult?> HeartbeatAsync(
        string credential,
        FarmAgentHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = Validate(request);
        if (string.IsNullOrWhiteSpace(credential)) return null;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var credentialHash = HashSecret(credential);
        var node = await db.FarmNodes.SingleOrDefaultAsync(
            x => x.AgentCredentialHash == credentialHash && x.ConnectionKind == "Agent",
            cancellationToken);
        if (node is null) return null;

        var now = timeProvider.GetUtcNow();
        node.Runtime = normalized.Runtime;
        node.LogicalProcessors = normalized.LogicalProcessors;
        node.MemoryBytes = normalized.MemoryBytes;
        node.AgentVersion = normalized.AgentVersion;
        node.LastSeenAtUtc = now;
        if (node.Status == FarmNodeStatus.Offline.ToString())
            node.Status = FarmNodeStatus.Online.ToString();
        await db.SaveChangesAsync(cancellationToken);

        var status = Enum.Parse<FarmNodeStatus>(node.Status);
        return new FarmAgentHeartbeatResult(
            node.Id,
            status,
            status == FarmNodeStatus.Online,
            now,
            HeartbeatIntervalSeconds);
    }

    private static FarmAgentEnrollmentRequest Validate(FarmAgentEnrollmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new FarmAgentEnrollmentRequest(
            Required(request.Name, nameof(request.Name), 3, 160),
            Required(request.Runtime, nameof(request.Runtime), 3, 500),
            Positive(request.LogicalProcessors, nameof(request.LogicalProcessors), 1024),
            Positive(request.MemoryBytes, nameof(request.MemoryBytes), 16L * 1024 * 1024 * 1024 * 1024),
            Required(request.AgentVersion, nameof(request.AgentVersion), 1, 80));
    }

    private static FarmAgentHeartbeatRequest Validate(FarmAgentHeartbeatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new FarmAgentHeartbeatRequest(
            Required(request.Runtime, nameof(request.Runtime), 3, 500),
            Positive(request.LogicalProcessors, nameof(request.LogicalProcessors), 1024),
            Positive(request.MemoryBytes, nameof(request.MemoryBytes), 16L * 1024 * 1024 * 1024 * 1024),
            Required(request.AgentVersion, nameof(request.AgentVersion), 1, 80));
    }

    private static string Required(string value, string name, int minimum, int maximum)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length < minimum || normalized.Length > maximum)
            throw new ArgumentException($"{name} deve possuir entre {minimum} e {maximum} caracteres.", name);
        return normalized;
    }

    private static T Positive<T>(T value, string name, T maximum)
        where T : struct, INumber<T>
    {
        if (value <= T.Zero || value > maximum)
            throw new ArgumentOutOfRangeException(name);
        return value;
    }

    private static string CreateSecret(string prefix)
    {
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{prefix}_{value}";
    }

    internal static string HashSecret(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
