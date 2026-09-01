using atos.AIFarm.Infrastructure.Persistence;

namespace atos.AIFarm.UnitTests;

public sealed class FarmManifestSecurityTests
{
    private static readonly FarmManifestSigningOptions Signing = new(
        Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()));

    [Fact]
    public void SignedCanonicalManifestVerifiesAndRoundTrips()
    {
        var manifest = CreateManifest();

        var signed = FarmManifestSecurity.Sign(manifest, Signing);
        var verified = FarmManifestSecurity.TryVerify(
            signed.Payload,
            signed.Digest,
            signed.Signature,
            Signing,
            out var restored);

        Assert.True(verified);
        Assert.Equal(manifest, restored);
        Assert.Equal(64, signed.Digest.Length);
        Assert.Equal(64, signed.Signature.Length);
    }

    [Fact]
    public void PayloadOrKeyTamperingInvalidatesSignature()
    {
        var signed = FarmManifestSecurity.Sign(CreateManifest(), Signing);
        var otherKey = new FarmManifestSigningOptions(
            Convert.ToBase64String(Enumerable.Repeat((byte)99, 32).ToArray()));

        Assert.False(FarmManifestSecurity.TryVerify(
            signed.Payload.Replace("SHA256_CHAIN", "UNSAFE_WORKLOAD", StringComparison.Ordinal),
            signed.Digest,
            signed.Signature,
            Signing,
            out _));
        Assert.False(FarmManifestSecurity.TryVerify(
            signed.Payload,
            signed.Digest,
            signed.Signature,
            otherKey,
            out _));
    }

    [Fact]
    public void SigningKeyRequiresAtLeast256Bits()
    {
        var shortKey = Convert.ToBase64String(new byte[16]);

        Assert.Throws<ArgumentException>(() => new FarmManifestSigningOptions(shortKey));
        Assert.Throws<ArgumentException>(() => new FarmManifestSigningOptions("not-base64"));
    }

    private static FarmJobManifest CreateManifest() => new(
        "atos-farm-manifest-v1",
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        "SHA256_CHAIN",
        "atos-sha256-chain-v1",
        "DETERMINISTIC_REEXECUTION_V1",
        "00112233445566778899aabbccddeeff",
        100_000,
        30,
        1,
        256L * 1024 * 1024,
        "DENY_ALL",
        "STANDARD",
        DateTimeOffset.Parse("2026-09-01T10:00:00Z"),
        DateTimeOffset.Parse("2026-09-01T10:05:00Z"));
}
